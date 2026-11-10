using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class PerformanceSettingsPolicyTests
{
    [Fact]
    public void Defaults_ResolveToResourceSaverBudget()
    {
        var settings = new AppSettings();

        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);

        Assert.Equal(
            PerformanceSettingsPolicy.ModeResourceSaver,
            effective.Mode);
        Assert.Equal(30, effective.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(60, effective.HiddenDeepCleanupDelaySeconds);
        Assert.Equal(5 * 60, effective.VisibleIdleCacheCleanupDelaySeconds);
        Assert.Equal(2 * 60, effective.TransientWindowReleaseDelaySeconds);
        Assert.Equal(
            PerformanceSettingsPolicy.CacheBudgetSmall,
            effective.CacheBudget);
        Assert.Equal(
            PerformanceSettingsPolicy.HiddenCacheCleanupScopeWarm,
            effective.HiddenCacheCleanupScope);
        Assert.True(effective.AllowTextMarqueeAnimations);
        Assert.True(effective.AllowVinylRotationAnimations);
        Assert.True(effective.AllowGlanceImageAutoRotation);
        Assert.True(effective.AllowCompactAmbientAnimations);
        Assert.False(PerformanceSettingsPolicy.Normalize(settings));
    }

    [Fact]
    public void ResourceSaver_UsesSmallBoundedCachesWithoutChangingInteractionAnimations()
    {
        var settings = new AppSettings
        {
            EnableTextMarqueeAnimations = true,
            EnableVinylRotationAnimations = false,
            EnableGlanceImageAutoRotation = true,
            EnableCompactAmbientAnimations = false,
            WidgetAnimationEffect = "Fade",
            WidgetAnimationSpeed = "Slow",
            WidgetCompactAnimationEffect = "Snappy",
            WidgetCompactAnimationDurationMs = 777,
            WidgetCompactExpandDelayMs = 333,
            WidgetCompactCollapseDelayMs = 888
        };

        PerformanceSettingsPolicy.ApplyPreset(
            settings,
            PerformanceSettingsPolicy.ModeResourceSaver);
        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);

        Assert.Equal(30, effective.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(60, effective.HiddenDeepCleanupDelaySeconds);
        Assert.Equal(5 * 60, effective.VisibleIdleCacheCleanupDelaySeconds);
        Assert.Equal(2 * 60, effective.TransientWindowReleaseDelaySeconds);
        Assert.Equal(
            PerformanceSettingsPolicy.CacheBudgetSmall,
            effective.CacheBudget);
        Assert.Equal(
            PerformanceSettingsPolicy.HiddenCacheCleanupScopeWarm,
            effective.HiddenCacheCleanupScope);
        Assert.True(effective.AllowTextMarqueeAnimations);
        Assert.False(effective.AllowVinylRotationAnimations);
        Assert.True(effective.AllowGlanceImageAutoRotation);
        Assert.False(effective.AllowCompactAmbientAnimations);
        Assert.True(settings.EnableTextMarqueeAnimations);
        Assert.False(settings.EnableVinylRotationAnimations);
        Assert.True(settings.EnableGlanceImageAutoRotation);
        Assert.False(settings.EnableCompactAmbientAnimations);
        Assert.False(settings.EnableContinuousDecorativeAnimations);
        Assert.Equal("Fade", settings.WidgetAnimationEffect);
        Assert.Equal("Slow", settings.WidgetAnimationSpeed);
        Assert.Equal("Snappy", settings.WidgetCompactAnimationEffect);
        Assert.Equal(777, settings.WidgetCompactAnimationDurationMs);
        Assert.Equal(333, settings.WidgetCompactExpandDelayMs);
        Assert.Equal(888, settings.WidgetCompactCollapseDelayMs);
    }

    [Fact]
    public void RetiredBestExperience_NormalizesToBalanced()
    {
        var settings = new AppSettings
        {
            PerformanceMode = PerformanceSettingsPolicy.ModeBestVisual,
            VisibleIdleCacheCleanupDelaySeconds =
                PerformanceSettingsPolicy.CleanupNever,
            TransientWindowReleaseDelaySeconds =
                PerformanceSettingsPolicy.CleanupNever
        };

        Assert.True(PerformanceSettingsPolicy.Normalize(settings));
        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);

        Assert.Equal(PerformanceSettingsPolicy.ModeBalanced, settings.PerformanceMode);
        Assert.Equal(30, effective.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(5 * 60, effective.HiddenDeepCleanupDelaySeconds);
        Assert.Equal(
            PerformanceSettingsPolicy.CleanupAfter10Minutes,
            effective.VisibleIdleCacheCleanupDelaySeconds);
        Assert.Equal(
            PerformanceSettingsPolicy.CleanupAfter10Minutes,
            effective.TransientWindowReleaseDelaySeconds);
        Assert.Equal(
            PerformanceSettingsPolicy.CacheBudgetBalanced,
            effective.CacheBudget);
        Assert.True(effective.AllowTextMarqueeAnimations);
        Assert.True(effective.AllowVinylRotationAnimations);
        Assert.True(effective.AllowGlanceImageAutoRotation);
        Assert.True(effective.AllowCompactAmbientAnimations);
    }

    [Fact]
    public void Custom_RepairsRetiredNeverValuesToLongestFiniteChoices()
    {
        var settings = new AppSettings
        {
            PerformanceMode = PerformanceSettingsPolicy.ModeCustom,
            HiddenCacheCleanupDelaySeconds =
                PerformanceSettingsPolicy.CleanupNever,
            VisibleIdleCacheCleanupDelaySeconds =
                PerformanceSettingsPolicy.CleanupNever,
            TransientWindowReleaseDelaySeconds =
                PerformanceSettingsPolicy.CleanupNever,
            PerformanceCacheBudget =
                PerformanceSettingsPolicy.CacheBudgetLarge,
            EnableTextMarqueeAnimations = false,
            EnableVinylRotationAnimations = true,
            EnableGlanceImageAutoRotation = false,
            EnableCompactAmbientAnimations = true
        };

        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);

        Assert.Equal(
            PerformanceSettingsPolicy.CleanupAfter5Minutes,
            effective.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(
            PerformanceSettingsPolicy.CleanupAfter10Minutes,
            effective.HiddenDeepCleanupDelaySeconds);
        Assert.Equal(
            PerformanceSettingsPolicy.CleanupAfter15Minutes,
            effective.VisibleIdleCacheCleanupDelaySeconds);
        Assert.Equal(
            PerformanceSettingsPolicy.CleanupAfter10Minutes,
            effective.TransientWindowReleaseDelaySeconds);
        Assert.Equal(
            PerformanceSettingsPolicy.CacheBudgetLarge,
            effective.CacheBudget);
        Assert.Equal(
            PerformanceSettingsPolicy.HiddenCacheCleanupScopeWarm,
            effective.HiddenCacheCleanupScope);
        Assert.False(effective.AllowTextMarqueeAnimations);
        Assert.True(effective.AllowVinylRotationAnimations);
        Assert.False(effective.AllowGlanceImageAutoRotation);
        Assert.True(effective.AllowCompactAmbientAnimations);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void Custom_AcceptsShortVisibleAndTransientReleaseDelays(
        int delaySeconds)
    {
        var settings = new AppSettings
        {
            PerformanceMode = PerformanceSettingsPolicy.ModeCustom,
            VisibleIdleCacheCleanupDelaySeconds = delaySeconds,
            TransientWindowReleaseDelaySeconds = delaySeconds
        };

        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);

        Assert.Equal(delaySeconds, effective.VisibleIdleCacheCleanupDelaySeconds);
        Assert.Equal(delaySeconds, effective.TransientWindowReleaseDelaySeconds);
    }

    [Theory]
    [InlineData(PerformanceSettingsPolicy.CacheBudgetSmall, 1)]
    [InlineData(PerformanceSettingsPolicy.CacheBudgetBalanced, 1)]
    [InlineData(PerformanceSettingsPolicy.CacheBudgetLarge, 2)]
    public void CacheBudget_ControlsInactiveGroupContentRetention(
        string cacheBudget,
        int expectedCapacity)
    {
        Assert.Equal(
            expectedCapacity,
            PerformanceSettingsPolicy.ResolveInactiveGroupContentCacheCapacity(
                cacheBudget));
    }

    [Fact]
    public void Normalize_RepairsUnknownValuesToBalancedDefaults()
    {
        var settings = new AppSettings
        {
            PerformanceMode = "unknown",
            HiddenCacheCleanupDelaySeconds = 17,
            VisibleIdleCacheCleanupDelaySeconds = 18,
            TransientWindowReleaseDelaySeconds = 19,
            PerformanceCacheBudget = "unbounded",
            EnableTextMarqueeAnimations = false,
            EnableVinylRotationAnimations = false,
            EnableGlanceImageAutoRotation = false,
            EnableCompactAmbientAnimations = false
        };

        Assert.True(PerformanceSettingsPolicy.Normalize(settings));

        Assert.Equal(
            PerformanceSettingsPolicy.ModeBalanced,
            settings.PerformanceMode);
        Assert.Equal(
            PerformanceSettingsPolicy.CleanupAfter30Seconds,
            settings.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(
            PerformanceSettingsPolicy.CleanupAfter10Minutes,
            settings.VisibleIdleCacheCleanupDelaySeconds);
        Assert.Equal(
            PerformanceSettingsPolicy.CleanupAfter10Minutes,
            settings.TransientWindowReleaseDelaySeconds);
        Assert.Equal(
            PerformanceSettingsPolicy.CacheBudgetBalanced,
            settings.PerformanceCacheBudget);
        Assert.False(settings.EnableTextMarqueeAnimations);
        Assert.False(settings.EnableVinylRotationAnimations);
        Assert.False(settings.EnableGlanceImageAutoRotation);
        Assert.False(settings.EnableCompactAmbientAnimations);
        Assert.False(settings.EnableContinuousDecorativeAnimations);
    }

    [Fact]
    public void Custom_AllowsWarmHiddenCacheRetention()
    {
        var settings = new AppSettings
        {
            PerformanceMode = PerformanceSettingsPolicy.ModeCustom,
            HiddenCacheCleanupScope =
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeWarm
        };

        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);

        Assert.Equal(
            PerformanceSettingsPolicy.HiddenCacheCleanupScopeWarm,
            effective.HiddenCacheCleanupScope);
        Assert.False(PerformanceSettingsPolicy.Normalize(settings));
    }

    [Fact]
    public void Normalize_RepairsUnknownHiddenCleanupScopeToWarm()
    {
        var settings = new AppSettings
        {
            PerformanceMode = PerformanceSettingsPolicy.ModeCustom,
            HiddenCacheCleanupScope = "unknown"
        };

        Assert.True(PerformanceSettingsPolicy.Normalize(settings));
        Assert.Equal(
            PerformanceSettingsPolicy.HiddenCacheCleanupScopeWarm,
            settings.HiddenCacheCleanupScope);
    }

    [Theory]
    [InlineData(PerformanceSettingsPolicy.ModeBalanced)]
    [InlineData(PerformanceSettingsPolicy.ModeResourceSaver)]
    public void Presets_NormalizeCleanupPolicyWithoutOverwritingAnimationChoices(
        string mode)
    {
        var settings = new AppSettings
        {
            PerformanceMode = mode,
            HiddenCacheCleanupDelaySeconds = 17,
            VisibleIdleCacheCleanupDelaySeconds = 18,
            TransientWindowReleaseDelaySeconds = 19,
            PerformanceCacheBudget = "unbounded",
            EnableTextMarqueeAnimations = false,
            EnableVinylRotationAnimations = true,
            EnableGlanceImageAutoRotation = false,
            EnableCompactAmbientAnimations = true,
            EnableContinuousDecorativeAnimations = true
        };

        Assert.True(PerformanceSettingsPolicy.Normalize(settings));
        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(settings);

        Assert.False(settings.EnableTextMarqueeAnimations);
        Assert.True(settings.EnableVinylRotationAnimations);
        Assert.False(settings.EnableGlanceImageAutoRotation);
        Assert.True(settings.EnableCompactAmbientAnimations);
        Assert.False(settings.EnableContinuousDecorativeAnimations);
        Assert.False(effective.AllowTextMarqueeAnimations);
        Assert.True(effective.AllowVinylRotationAnimations);
        Assert.False(effective.AllowGlanceImageAutoRotation);
        Assert.True(effective.AllowCompactAmbientAnimations);
    }

    [Fact]
    public void SupportedUserCleanupDelays_AreAlwaysFinite()
    {
        int[] normalized =
        [
            PerformanceSettingsPolicy.NormalizeHiddenCacheCleanupDelaySeconds(
                PerformanceSettingsPolicy.CleanupNever),
            PerformanceSettingsPolicy.NormalizeVisibleIdleCacheCleanupDelaySeconds(
                PerformanceSettingsPolicy.CleanupNever),
            PerformanceSettingsPolicy.NormalizeTransientWindowReleaseDelaySeconds(
                PerformanceSettingsPolicy.CleanupNever)
        ];

        Assert.All(normalized, value => Assert.True(value > 0));
        Assert.Equal(5 * 60, normalized[0]);
        Assert.Equal(15 * 60, normalized[1]);
        Assert.Equal(10 * 60, normalized[2]);
    }
}
