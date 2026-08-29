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
        string featureOptions = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/SettingsViewModel.FeatureOptions.cs"));
        string settingsWindow = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string backdrop = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Backdrop.cs"));
        string compatibility = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WindowsCompatibilityService.cs"));

        Assert.Contains("WindowsCompatibilityService.IsWindows11OrLater", featureOptions, StringComparison.Ordinal);
        Assert.Contains("[MaterialAcrylic, MaterialAcrylicBase, MaterialSolid]", featureOptions, StringComparison.Ordinal);
        Assert.Contains("IsOpen=\"{Binding IsWindows10VisualCompatibilityMode}\"", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding SupportsNativeWidgetCorners}\"", settingsWindow, StringComparison.Ordinal);
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
    public void WidgetWindow_UsesWinUIExTransparentTintBackdropForSolidMaterial()
    {
        string project = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"));
        string baseWindow = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.cs"));
        string backdrop = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Backdrop.cs"));
        string contentWindow = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        string quickCapture = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/QuickCaptureWidgetWindow.Appearance.cs"));

        Assert.Contains("<PackageReference Include=\"WinUIEx\" Version=\"2.9.3\" />", project, StringComparison.Ordinal);
        Assert.Contains("WinUIEx.TransparentTintBackdrop? _solidColorBackdrop", baseWindow, StringComparison.Ordinal);
        Assert.Contains("new WinUIEx.TransparentTintBackdrop(tintColor)", backdrop, StringComparison.Ordinal);
        Assert.Contains("SystemBackdrop = _solidColorBackdrop", backdrop, StringComparison.Ordinal);
        Assert.Contains("ClearSolidColorBackdrop();", baseWindow, StringComparison.Ordinal);
        Assert.Contains("!IsSolidColorBackdropActive", contentWindow, StringComparison.Ordinal);
        Assert.Contains("!IsSolidColorBackdropActive", quickCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyTransparentAcrylicController", backdrop, StringComparison.Ordinal);
    }
}
