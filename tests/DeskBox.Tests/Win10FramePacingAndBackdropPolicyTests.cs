using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class Win10FramePacingAndBackdropPolicyTests
{
    [Theory]
    [InlineData(60, 16.67)]
    [InlineData(120, 8.33)]
    [InlineData(144, 6.94)]
    [InlineData(165, 6.06)]
    [InlineData(240, 4.17)]
    [InlineData(360, 4.0)]
    [InlineData(0, 16.67)]
    [InlineData(-5, 16.67)]
    public void ResolveFrameTickInterval_MatchesNativeRefreshCadence(
        int refreshRateHz,
        double expectedMs)
    {
        TimeSpan interval = WidgetDisplayRefreshRatePolicy.ResolveFrameTickInterval(refreshRateHz);
        Assert.Equal(expectedMs, interval.TotalMilliseconds, precision: 2);
    }

    [Fact]
    public void ResolveFrameTickInterval_ClampsToMinimumTickForHighRefresh()
    {
        TimeSpan interval = WidgetDisplayRefreshRatePolicy.ResolveFrameTickInterval(500);
        Assert.Equal(
            WidgetDisplayRefreshRatePolicy.MinimumFrameTickMs,
            interval.TotalMilliseconds,
            precision: 3);
    }

    [Theory]
    [InlineData(144, 1)]
    [InlineData(240, 1)]
    [InlineData(60, 1)]
    public void ResolveSkip_FullRateLevelKeepsNativeCadence(int refreshRateHz, int expectedSkip)
    {
        Assert.Equal(
            expectedSkip,
            WidgetCompactFrameSkipPolicy.ResolveSkip(
                refreshRateHz,
                WidgetCompactFrameSkipPolicy.FullRateLevel));
    }

    [Theory]
    [InlineData(144, 2, 2)]
    [InlineData(144, 3, 5)]
    [InlineData(240, 2, 4)]
    [InlineData(240, 3, 8)]
    [InlineData(60, 2, 1)]
    [InlineData(60, 3, 2)]
    public void ResolveSkip_EscalatedLevelsTargetCleanFrameRates(
        int refreshRateHz,
        int level,
        int expectedSkip)
    {
        Assert.Equal(expectedSkip, WidgetCompactFrameSkipPolicy.ResolveSkip(refreshRateHz, level));
    }

    [Theory]
    [InlineData(6, 8, true)]
    [InlineData(5, 8, false)]
    [InlineData(6, 7, false)]
    [InlineData(8, 0, false)]
    public void ShouldEscalate_RequiresFullWindowAndMajorityOverruns(
        int overrunTicks,
        int sampledTicks,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactFrameSkipPolicy.ShouldEscalate(overrunTicks, sampledTicks));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    public void Escalate_StopsAtThirtyFpsFloor(int level, int expected)
    {
        Assert.Equal(expected, WidgetCompactFrameSkipPolicy.Escalate(level));
    }

    [Fact]
    public void IsOverrun_UsesBudgetFactorThreshold()
    {
        double budgetMs = 6.94;
        Assert.False(WidgetCompactFrameSkipPolicy.IsOverrun(10.40, budgetMs));
        Assert.True(WidgetCompactFrameSkipPolicy.IsOverrun(10.42, budgetMs));
    }

    [Fact]
    public void BackdropSimplification_KeepsFrostedGlassByDefault()
    {
        Assert.False(InteractionBackdropSimplificationPolicy.ShouldSimplify(
            recentOverrunMask: 0,
            performanceMode: PerformanceSettingsPolicy.ModeBalanced));
    }

    [Fact]
    public void BackdropSimplification_ResourceSaverForcesTintOnlyAccent()
    {
        Assert.True(InteractionBackdropSimplificationPolicy.ShouldSimplify(
            recentOverrunMask: 0,
            performanceMode: PerformanceSettingsPolicy.ModeResourceSaver));
    }

    [Fact]
    public void BackdropSimplification_RequiresSustainedRecentOverruns()
    {
        long twentyFourOverruns = (1L << 24) - 1;
        Assert.True(InteractionBackdropSimplificationPolicy.ShouldSimplify(
            twentyFourOverruns,
            PerformanceSettingsPolicy.ModeBalanced));
        long twentyThreeOverruns = (1L << 23) - 1;
        Assert.False(InteractionBackdropSimplificationPolicy.ShouldSimplify(
            twentyThreeOverruns,
            PerformanceSettingsPolicy.ModeBalanced));
    }

    [Theory]
    [InlineData(true, true, 0.5, Win32Helper.ACCENT_ENABLE_ACRYLICBLURBEHIND)]
    [InlineData(true, true, 0.005, Win32Helper.ACCENT_ENABLE_BLURBEHIND)]
    [InlineData(true, false, 0.5, Win32Helper.ACCENT_ENABLE_TRANSPARENTGRADIENT)]
    [InlineData(true, false, 0.005, Win32Helper.ACCENT_ENABLE_TRANSPARENTGRADIENT)]
    [InlineData(false, true, 0.0, Win32Helper.ACCENT_DISABLED)]
    [InlineData(false, true, 0.5, Win32Helper.ACCENT_ENABLE_GRADIENT)]
    public void ResolveAccentState_CoversInteractionSimplificationMatrix(
        bool enabled,
        bool blurEnabled,
        double opacity,
        int expectedState)
    {
        Assert.Equal(expectedState, Win32Helper.ResolveAccentState(enabled, blurEnabled, opacity));
    }
}
