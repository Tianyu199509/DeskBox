using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SolidColorBackdropTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 128)]
    [InlineData(1.0, 255)]
    public void SolidSurfaceColor_MapsOpacityAcrossTheFullAlphaRange(
        double opacity,
        int expectedAlpha)
    {
        Windows.UI.Color color = WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
            isDark: false,
            Windows.UI.Color.FromArgb(255, 0, 120, 215),
            opacity);

        Assert.Equal(expectedAlpha, color.A);
    }

    [Fact]
    public void LegacyAcrylicOpacity_UsesTheFullSurfaceRangeAndMaterialIntensity()
    {
        double transparent = WidgetMaterialVisualCalculator.CalculateLegacyAcrylicOpacity(
            useBase: false,
            surfaceOpacity: 0,
            materialIntensity: 0.5);
        double middle = WidgetMaterialVisualCalculator.CalculateLegacyAcrylicOpacity(
            useBase: false,
            surfaceOpacity: 0.5,
            materialIntensity: 0.5);
        double opaque = WidgetMaterialVisualCalculator.CalculateLegacyAcrylicOpacity(
            useBase: false,
            surfaceOpacity: 1,
            materialIntensity: 0.5);
        double stronger = WidgetMaterialVisualCalculator.CalculateLegacyAcrylicOpacity(
            useBase: false,
            surfaceOpacity: 0.5,
            materialIntensity: 1);
        double baseAcrylic = WidgetMaterialVisualCalculator.CalculateLegacyAcrylicOpacity(
            useBase: true,
            surfaceOpacity: 0.5,
            materialIntensity: 0.5);

        Assert.True(transparent < middle);
        Assert.True(middle < opaque);
        Assert.True(stronger > middle);
        Assert.True(baseAcrylic > middle);
    }

    [Fact]
    public void LegacyAcrylicSurfaceOverlay_KeepsOpacityAndIntensityVisiblyEffective()
    {
        Windows.UI.Color accent = Windows.UI.Color.FromArgb(255, 0, 120, 215);
        Windows.UI.Color transparent =
            WidgetMaterialVisualCalculator.BuildLegacyAcrylicSurfaceOverlayColor(
                isDark: false,
                accent,
                useBase: false,
                surfaceOpacity: 0,
                materialIntensity: 0.5);
        Windows.UI.Color middle =
            WidgetMaterialVisualCalculator.BuildLegacyAcrylicSurfaceOverlayColor(
                isDark: false,
                accent,
                useBase: false,
                surfaceOpacity: 0.5,
                materialIntensity: 0.5);
        Windows.UI.Color opaque =
            WidgetMaterialVisualCalculator.BuildLegacyAcrylicSurfaceOverlayColor(
                isDark: false,
                accent,
                useBase: false,
                surfaceOpacity: 1,
                materialIntensity: 0.5);
        Windows.UI.Color stronger =
            WidgetMaterialVisualCalculator.BuildLegacyAcrylicSurfaceOverlayColor(
                isDark: false,
                accent,
                useBase: false,
                surfaceOpacity: 0.5,
                materialIntensity: 1);

        Assert.True(transparent.A < middle.A);
        Assert.True(middle.A < opaque.A);
        Assert.True(stronger.A > middle.A);
        Assert.True(opaque.A < byte.MaxValue);
    }

    [Fact]
    public void Windows10Appearance_UsesCapabilityFirstAcrylicWithLegacyFallback()
    {
        _ = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string appearanceSection = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml"));
        string backdrop = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Backdrop.cs"));
        string compatibility = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WindowsCompatibilityService.cs"));

        string appearanceOptions = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/SettingsViewModel.AppearanceOptions.cs"));
        Assert.Contains("WindowsCompatibilityService.IsWindows11OrLater", appearanceOptions, StringComparison.Ordinal);
        Assert.Contains("SettingsService.WidgetMaterialTypeSolid", appearanceOptions, StringComparison.Ordinal);
        Assert.Contains("IsOpen=\"{Binding Windows10Compatibility}\"", appearanceSection, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding NativeCornersSupported}\"", appearanceSection, StringComparison.Ordinal);
        Assert.Contains("ResolveWidgetMaterialType", backdrop, StringComparison.Ordinal);
        Assert.Contains("ApplyAcrylicController", backdrop, StringComparison.Ordinal);
        Assert.Contains("WindowsCompatibilityService.SupportsDesktopAcrylic", backdrop, StringComparison.Ordinal);
        Assert.Contains("CalculateLegacyAcrylicOpacity", backdrop, StringComparison.Ordinal);
        Assert.Contains("LegacyAccentBackdropActive = ApplyLegacyAccentBackdrop", backdrop, StringComparison.Ordinal);
        Assert.Contains("return Win32Helper.ApplyAccentBlur(HWnd, tintColor, opacity, enabled: true)", backdrop, StringComparison.Ordinal);
        Assert.Contains("SystemBackdrop = null", backdrop, StringComparison.Ordinal);
        Assert.DoesNotContain("!WindowsCompatibilityService.UsesLegacyWindowAcrylic)", backdrop, StringComparison.Ordinal);
        Assert.Contains("DesktopAcrylicController.IsSupported()", compatibility, StringComparison.Ordinal);
        Assert.DoesNotContain("IsWindows11OrLater && IsSupported(() => DesktopAcrylicController.IsSupported())", compatibility, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetWindow_UsesNativeSolidTintSystemBackdropForSolidMaterial()
    {
        string materialBackdrop = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Services/WidgetMaterialSystemBackdrop.cs"));
        string baseWindow = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.cs"));
        string backdrop = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Backdrop.cs"));
        string popoverHost = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/StackPopoverHostWindow.cs"));
        string contentWindow = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));

        Assert.Contains("internal sealed partial class SolidTintSystemBackdrop : SystemBackdrop, IDisposable", materialBackdrop, StringComparison.Ordinal);
        Assert.Contains("compositor.CreateColorBrush(_tintColor)", materialBackdrop, StringComparison.Ordinal);
        Assert.Contains("SolidTintSystemBackdrop? _solidColorBackdrop", baseWindow, StringComparison.Ordinal);
        Assert.Contains("new SolidTintSystemBackdrop(tintColor)", backdrop, StringComparison.Ordinal);
        Assert.Contains("SystemBackdrop = _solidColorBackdrop", backdrop, StringComparison.Ordinal);
        Assert.Contains("ClearSolidColorBackdrop();", baseWindow, StringComparison.Ordinal);
        Assert.Contains("!IsSolidColorBackdropActive", contentWindow, StringComparison.Ordinal);
        Assert.Contains("SolidTintSystemBackdrop", popoverHost, StringComparison.Ordinal);
        Assert.DoesNotContain("WinUIEx", baseWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("WinUIEx", backdrop, StringComparison.Ordinal);
        Assert.DoesNotContain("WinUIEx", popoverHost, StringComparison.Ordinal);
        Assert.DoesNotContain("_inactiveBackdropCleanupTimer", baseWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ScheduleInactiveBackdropControllerCleanup", backdrop, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyTransparentAcrylicController", backdrop, StringComparison.Ordinal);
    }
}
