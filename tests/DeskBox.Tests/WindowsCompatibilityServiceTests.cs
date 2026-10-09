using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WindowsCompatibilityServiceTests
{
    [Theory]
    [InlineData(SettingsService.WidgetCornerPreferenceRound, 19045, SettingsService.WidgetCornerPreferenceSquare)]
    [InlineData(SettingsService.WidgetCornerPreferenceSmall, 19045, SettingsService.WidgetCornerPreferenceSquare)]
    [InlineData(SettingsService.WidgetCornerPreferenceSquare, 19045, SettingsService.WidgetCornerPreferenceSquare)]
    [InlineData(null, 19045, SettingsService.WidgetCornerPreferenceSquare)]
    [InlineData(SettingsService.WidgetCornerPreferenceRound, 21999, SettingsService.WidgetCornerPreferenceSquare)]
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
    [InlineData(SettingsService.WidgetMaterialTypeMica, 21999, SettingsService.WidgetMaterialTypeAcrylic)]
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

    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(2.25, 2.25)]
    [InlineData(3.0, 2.25)]
    [InlineData(double.NaN, 1.0)]
    public void NormalizeSystemTextScaleFactor_ClampsToWindowsRange(
        double value,
        double expected)
    {
        Assert.Equal(
            expected,
            WindowsCompatibilityService.NormalizeSystemTextScaleFactor(value));
    }

    // Consumer-side guard pins: these were previously only protected by
    // string-contains contracts — deleting the guard condition itself did
    // not fail any test.

    [Theory]
    [InlineData(true, true, true, "Mica")]
    [InlineData(true, false, true, "Acrylic")]
    [InlineData(true, false, false, "Solid")]
    [InlineData(false, true, true, "Acrylic")]
    [InlineData(false, true, false, "Solid")]
    [InlineData(false, false, false, "Solid")]
    public void ResolveSafeBackdropKind_FallsThroughMicaAcrylicSolid(
        bool preferMica,
        bool supportsMica,
        bool supportsDesktopAcrylic,
        string expected)
    {
        Assert.Equal(
            expected,
            WindowsCompatibilityService.ResolveSafeBackdropKind(
                preferMica,
                supportsMica,
                supportsDesktopAcrylic));
    }

    [Theory]
    [InlineData(DeskBox.Platform.Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, false, true)]
    [InlineData(DeskBox.Platform.Win32Helper.DWMWA_BORDER_COLOR, false, true)]
    [InlineData(DeskBox.Platform.Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, false, true)]
    [InlineData(DeskBox.Platform.Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, true, false)]
    [InlineData(DeskBox.Platform.Win32Helper.DWMWA_BORDER_COLOR, true, false)]
    [InlineData(DeskBox.Platform.Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, true, false)]
    [InlineData(DeskBox.Platform.Win32Helper.DWMWA_USE_IMMERSIVE_DARK_MODE, false, false)]
    [InlineData(DeskBox.Platform.Win32Helper.DWMWA_TRANSITIONS_FORCEDISABLED, false, false)]
    [InlineData(DeskBox.Platform.Win32Helper.DWMWA_CLOAK, false, false)]
    [InlineData(DeskBox.Platform.Win32Helper.DWMWA_NCRENDERING_POLICY, true, false)]
    public void ShouldSkipDwmWindowAttribute_OnlyDropsWin11AttributesOnWin10(
        int attr,
        bool supportsWin11DwmAttributes,
        bool expected)
    {
        Assert.Equal(
            expected,
            DeskBox.Platform.Win32Helper.ShouldSkipDwmWindowAttribute(
                attr,
                supportsWin11DwmAttributes));
    }

    [Fact]
    public void ResolveSupportedMaterialKinds_DropsMicaKindsBelowWindows11()
    {
        string[] win10 = DeskBox.ViewModels.SettingsViewModel.ResolveSupportedMaterialKinds(
            isWindows11OrLater: false);

        Assert.Equal(
            new[]
            {
                SettingsService.WidgetMaterialTypeAcrylic,
                SettingsService.WidgetMaterialTypeAcrylicBase,
                SettingsService.WidgetMaterialTypeSolid
            },
            win10);
        Assert.DoesNotContain(SettingsService.WidgetMaterialTypeMica, win10);
        Assert.DoesNotContain(SettingsService.WidgetMaterialTypeMicaAlt, win10);
    }

    [Fact]
    public void ResolveSupportedMaterialKinds_OffersAllFiveKindsOnWindows11()
    {
        Assert.Equal(
            new[]
            {
                SettingsService.WidgetMaterialTypeAcrylic,
                SettingsService.WidgetMaterialTypeAcrylicBase,
                SettingsService.WidgetMaterialTypeMica,
                SettingsService.WidgetMaterialTypeMicaAlt,
                SettingsService.WidgetMaterialTypeSolid
            },
            DeskBox.ViewModels.SettingsViewModel.ResolveSupportedMaterialKinds(
                isWindows11OrLater: true));
    }
}
