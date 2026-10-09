using DeskBox.Models;

namespace DeskBox.Tests;

public sealed class WidgetContentTransitionProfileTests
{
    [Fact]
    public void DirectionalSwitch_UsesRestrainedMotionWithinDesignDuration()
    {
        WidgetContentTransitionProfile profile =
            WidgetContentTransitionProfile.Create(
                animationsEnabled: true,
                directional: true);

        Assert.True(profile.UsesMotion);
        Assert.Equal(210, profile.DurationMilliseconds);
        Assert.Equal(78, profile.OutgoingDurationMilliseconds);
        Assert.Equal(12, profile.SwapGapMilliseconds);
        Assert.Equal(120, profile.IncomingDurationMilliseconds);
        Assert.Equal(6, profile.TranslationDistance);
        Assert.Equal(0.975, profile.MinimumScale);
        Assert.Equal(0, profile.IncomingStartOpacity);
        Assert.Equal(0, profile.OutgoingEndOpacity);
    }

    [Fact]
    public void DirectSelection_UsesMutuallyExclusiveScaleWithoutTranslation()
    {
        WidgetContentTransitionProfile profile =
            WidgetContentTransitionProfile.Create(
                animationsEnabled: true,
                directional: false);

        Assert.False(profile.UsesMotion);
        Assert.Equal(210, profile.DurationMilliseconds);
        Assert.Equal(0, profile.TranslationDistance);
        Assert.Equal(0.975, profile.MinimumScale);
        Assert.Equal(0, profile.IncomingStartOpacity);
        Assert.Equal(0, profile.OutgoingEndOpacity);
    }

    [Fact]
    public void AnimatedSwitch_LeavesAnExplicitZeroOverlapSwapGap()
    {
        WidgetContentTransitionProfile profile =
            WidgetContentTransitionProfile.Create(
                animationsEnabled: true,
                directional: true);

        Assert.True(profile.SwapGapMilliseconds > 0);
        Assert.Equal(
            profile.DurationMilliseconds,
            profile.OutgoingDurationMilliseconds +
            profile.SwapGapMilliseconds +
            profile.IncomingDurationMilliseconds);
        Assert.Equal(0, profile.IncomingStartOpacity);
        Assert.Equal(0, profile.OutgoingEndOpacity);
    }

    [Fact]
    public void ReducedMotion_SwitchesImmediatelyWithoutOverlap()
    {
        WidgetContentTransitionProfile profile =
            WidgetContentTransitionProfile.Create(
                animationsEnabled: false,
                directional: true);

        Assert.False(profile.UsesMotion);
        Assert.Equal(0, profile.DurationMilliseconds);
        Assert.Equal(0, profile.OutgoingDurationMilliseconds);
        Assert.Equal(0, profile.SwapGapMilliseconds);
        Assert.Equal(0, profile.IncomingDurationMilliseconds);
        Assert.Equal(0, profile.TranslationDistance);
        Assert.Equal(1, profile.MinimumScale);
    }

    [Fact]
    public void HorizontalSwitch_SlidesALongerDistanceOnTheSameTimeline()
    {
        WidgetContentTransitionProfile profile =
            WidgetContentTransitionProfile.Create(
                animationsEnabled: true,
                directional: true,
                WidgetGroupSwitchAnimationEffect.Horizontal);

        Assert.True(profile.UsesMotion);
        Assert.Equal(WidgetGroupSwitchAnimationEffect.Horizontal, profile.Effect);
        Assert.Equal(12, profile.TranslationDistance);
        // The timeline skeleton is shared with the vertical push so both axes
        // keep the same cadence.
        Assert.Equal(210, profile.DurationMilliseconds);
        Assert.Equal(78, profile.OutgoingDurationMilliseconds);
        Assert.Equal(12, profile.SwapGapMilliseconds);
        Assert.Equal(120, profile.IncomingDurationMilliseconds);
        Assert.Equal(0.975, profile.MinimumScale);
    }

    [Fact]
    public void HorizontalSwitch_WithoutDirectionalOrigin_KeepsTheScaleAndFadeOnly()
    {
        WidgetContentTransitionProfile profile =
            WidgetContentTransitionProfile.Create(
                animationsEnabled: true,
                directional: false,
                WidgetGroupSwitchAnimationEffect.Horizontal);

        Assert.False(profile.UsesMotion);
        Assert.Equal(0, profile.TranslationDistance);
        Assert.Equal(0.975, profile.MinimumScale);
    }

    [Fact]
    public void CrossFade_KeepsTheTimelineFlatWithoutScaleOrMotion()
    {
        WidgetContentTransitionProfile profile =
            WidgetContentTransitionProfile.Create(
                animationsEnabled: true,
                directional: true,
                WidgetGroupSwitchAnimationEffect.CrossFade);

        Assert.False(profile.UsesMotion);
        Assert.Equal(0, profile.TranslationDistance);
        Assert.Equal(1, profile.MinimumScale);
        Assert.Equal(210, profile.DurationMilliseconds);
    }

    [Fact]
    public void Suppress_SwapsInstantlyEvenForDirectionalSwitches()
    {
        WidgetContentTransitionProfile profile =
            WidgetContentTransitionProfile.Create(
                animationsEnabled: true,
                directional: true,
                WidgetGroupSwitchAnimationEffect.Suppress);

        Assert.False(profile.UsesMotion);
        Assert.Equal(0, profile.DurationMilliseconds);
        Assert.Equal(0, profile.TranslationDistance);
        Assert.Equal(1, profile.MinimumScale);
    }
}
