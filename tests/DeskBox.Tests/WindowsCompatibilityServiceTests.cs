using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WindowsCompatibilityServiceTests
{
    [Theory]
    [InlineData(SettingsService.WidgetCornerPreferenceRound, 19045, SettingsService.WidgetCornerPreferenceSquare)]
    [InlineData(SettingsService.WidgetCornerPreferenceSmall, 19045, SettingsService.WidgetCornerPreferenceSquare)]
    [InlineData(SettingsService.WidgetCornerPreferenceSquare, 19045, SettingsService.WidgetCornerPreferenceSquare)]
    [InlineData(null, 19045, SettingsService.WidgetCornerPreferenceSquare)]
    [InlineData(SettingsService.WidgetCornerPreferenceRound, 22000, SettingsService.WidgetCornerPreferenceRound)]
    [InlineData(SettingsService.WidgetCornerPreferenceSmall, 22631, SettingsService.WidgetCornerPreferenceSmall)]
    [InlineData(SettingsService.WidgetCornerPreferenceSquare, 26100, SettingsService.WidgetCornerPreferenceSquare)]
    [InlineData(null, 26100, SettingsService.WidgetCornerPreferenceRound)]
    [InlineData(SettingsService.WidgetCornerPreferenceRound, 0, SettingsService.WidgetCornerPreferenceSquare)]
    public void ResolveEffectiveWidgetCornerPreferenceForBuild_ForcesSquareOnWin10(
        string? requested,
        int osBuild,
        string expected)
    {
        Assert.Equal(
            expected,
            WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreferenceForBuild(
                requested,
                osBuild));
    }

    [Theory]
    [InlineData(SettingsService.WidgetCompactMediaCornerFollowWidget, 19045, SettingsService.WidgetCompactMediaCornerSquare)]
    [InlineData(SettingsService.WidgetCompactMediaCornerSmall, 19045, SettingsService.WidgetCompactMediaCornerSquare)]
    [InlineData(SettingsService.WidgetCompactMediaCornerRound, 19045, SettingsService.WidgetCompactMediaCornerSquare)]
    [InlineData(SettingsService.WidgetCompactMediaCornerRound, 22000, SettingsService.WidgetCompactMediaCornerRound)]
    [InlineData(SettingsService.WidgetCompactMediaCornerSmall, 26100, SettingsService.WidgetCompactMediaCornerSmall)]
    [InlineData("Unknown", 26100, SettingsService.WidgetCompactMediaCornerFollowWidget)]
    public void ResolveEffectiveWidgetCompactMediaCornerModeForBuild_ForcesSquareOnWin10(
        string? requested,
        int osBuild,
        string expected)
    {
        Assert.Equal(
            expected,
            WindowsCompatibilityService.ResolveEffectiveWidgetCompactMediaCornerModeForBuild(
                requested,
                osBuild));
    }

    [Theory]
    [InlineData(SettingsService.WidgetMaterialTypeMica, 19045, SettingsService.WidgetMaterialTypeAcrylic)]
    [InlineData(SettingsService.WidgetMaterialTypeMicaAlt, 19045, SettingsService.WidgetMaterialTypeAcrylic)]
    [InlineData(SettingsService.WidgetMaterialTypeAcrylic, 19045, SettingsService.WidgetMaterialTypeAcrylic)]
    [InlineData(SettingsService.WidgetMaterialTypeAcrylicBase, 19045, SettingsService.WidgetMaterialTypeAcrylicBase)]
    [InlineData(SettingsService.WidgetMaterialTypeSolid, 19045, SettingsService.WidgetMaterialTypeSolid)]
    [InlineData(SettingsService.WidgetMaterialTypeMica, 22000, SettingsService.WidgetMaterialTypeMica)]
    [InlineData(SettingsService.WidgetMaterialTypeMicaAlt, 26100, SettingsService.WidgetMaterialTypeMicaAlt)]
    [InlineData("Unknown", 19045, SettingsService.WidgetMaterialTypeAcrylic)]
    public void ResolveWidgetMaterialTypeForBuild_UsesAcrylicForWin10Mica(
        string requested,
        int osBuild,
        string expected)
    {
        Assert.Equal(
            expected,
            WindowsCompatibilityService.ResolveWidgetMaterialTypeForBuild(
                requested,
                osBuild));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public void ResolveShouldAnimate_RequiresEffectsAndDisablesHighContrastMotion(
        bool animationsEnabled,
        bool advancedEffectsEnabled,
        bool highContrast,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsCompatibilityService.ResolveShouldAnimate(
                animationsEnabled,
                advancedEffectsEnabled,
                highContrast));
    }
}
