using DeskBox.Models;

namespace DeskBox.Tests;

public sealed class WidgetGroupSwitchAnimationPolicyTests
{
    [Theory]
    [InlineData(null, WidgetGroupSwitchAnimationStyles.Auto)]
    [InlineData("", WidgetGroupSwitchAnimationStyles.Auto)]
    [InlineData("Nonsense", WidgetGroupSwitchAnimationStyles.Auto)]
    [InlineData(WidgetGroupSwitchAnimationStyles.Auto, WidgetGroupSwitchAnimationStyles.Auto)]
    [InlineData(WidgetGroupSwitchAnimationStyles.Vertical, WidgetGroupSwitchAnimationStyles.Vertical)]
    [InlineData(WidgetGroupSwitchAnimationStyles.Horizontal, WidgetGroupSwitchAnimationStyles.Horizontal)]
    [InlineData(WidgetGroupSwitchAnimationStyles.Fade, WidgetGroupSwitchAnimationStyles.Fade)]
    [InlineData(WidgetGroupSwitchAnimationStyles.None, WidgetGroupSwitchAnimationStyles.None)]
    public void Normalize_KeepsCanonicalValues_AndCollapsesInvalidToAuto(
        string? value,
        string expected)
    {
        Assert.Equal(expected, WidgetGroupSwitchAnimationStyles.Normalize(value));
    }

    [Theory]
    [InlineData(WidgetGroupSwitchAnimationStyles.Vertical)]
    [InlineData(WidgetGroupSwitchAnimationStyles.Horizontal)]
    [InlineData(WidgetGroupSwitchAnimationStyles.Fade)]
    [InlineData(WidgetGroupSwitchAnimationStyles.None)]
    public void ExplicitChoices_IgnoreTheNavigationStyle(string settingValue)
    {
        WidgetGroupSwitchAnimationEffect expected =
            settingValue switch
            {
                WidgetGroupSwitchAnimationStyles.Vertical => WidgetGroupSwitchAnimationEffect.Vertical,
                WidgetGroupSwitchAnimationStyles.Horizontal => WidgetGroupSwitchAnimationEffect.Horizontal,
                WidgetGroupSwitchAnimationStyles.Fade => WidgetGroupSwitchAnimationEffect.CrossFade,
                _ => WidgetGroupSwitchAnimationEffect.Suppress
            };

        Assert.Equal(
            expected,
            WidgetGroupSwitchAnimationPolicy.ResolveEffect(
                settingValue,
                WidgetGroupNavigationStyles.Tabs));
        Assert.Equal(
            expected,
            WidgetGroupSwitchAnimationPolicy.ResolveEffect(
                settingValue,
                WidgetGroupNavigationStyles.Stack));
    }

    [Fact]
    public void Auto_FollowsTheResolvedNavigationStyle()
    {
        // Flat tab strips are lateral navigation: they switch horizontally.
        Assert.Equal(
            WidgetGroupSwitchAnimationEffect.Horizontal,
            WidgetGroupSwitchAnimationPolicy.ResolveEffect(
                WidgetGroupSwitchAnimationStyles.Auto,
                WidgetGroupNavigationStyles.Tabs));

        // The collapsed stack keeps the vertical push.
        Assert.Equal(
            WidgetGroupSwitchAnimationEffect.Vertical,
            WidgetGroupSwitchAnimationPolicy.ResolveEffect(
                WidgetGroupSwitchAnimationStyles.Auto,
                WidgetGroupNavigationStyles.Stack));

        // Legacy "Auto" navigation style means Stack; invalid values are
        // normalized to the fresh-install Tabs default by the shared
        // navigation normalizer before resolving.
        Assert.Equal(
            WidgetGroupSwitchAnimationEffect.Vertical,
            WidgetGroupSwitchAnimationPolicy.ResolveEffect(
                WidgetGroupSwitchAnimationStyles.Auto,
                "Auto"));
        Assert.Equal(
            WidgetGroupSwitchAnimationEffect.Horizontal,
            WidgetGroupSwitchAnimationPolicy.ResolveEffect(
                WidgetGroupSwitchAnimationStyles.Auto,
                "Nonsense"));
    }

    [Fact]
    public void Auto_WithoutAPresentation_StaysVertical()
    {
        // Non-group content swaps keep their current motion when no group
        // presentation is available.
        Assert.Equal(
            WidgetGroupSwitchAnimationEffect.Vertical,
            WidgetGroupSwitchAnimationPolicy.ResolveEffect(
                WidgetGroupSwitchAnimationStyles.Auto,
                navigationStyle: null));
        Assert.Equal(
            WidgetGroupSwitchAnimationEffect.Vertical,
            WidgetGroupSwitchAnimationPolicy.ResolveEffect(
                null,
                navigationStyle: null));
    }
}
