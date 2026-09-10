extern alias GlancePkg;

using System.Text.Json;
using DeskBox.Helpers;
using DeskBox.Services;
using DeskBox.Services.Plugins;
using Windows.UI;
using PackageAppearance = GlancePkg::DeskBox.GlancePackage.Services.PackageAppearance;
using PackageMaterials = GlancePkg::DeskBox.Services.WidgetMaterialVisualCalculator;
using PackagePaletteService = GlancePkg::DeskBox.Services.GlanceImagePaletteService;
using PackagePalette = GlancePkg::DeskBox.Services.GlanceImagePalette;
using PackageDataFile = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceDataFile;

namespace DeskBox.Tests;

public sealed class NativeGlanceAppearanceTests
{
    [Theory]
    [InlineData("Light", "MicaAlt", 0.3, 0.9)]
    [InlineData("Dark", "AcrylicBase", 1, 0)]
    [InlineData("Light", "Solid", 0.6, 0.65)]
    public void HostTokensReachTheActualPackageParser(string theme, string material, double opacity, double intensity)
    {
        Color accent = Color.FromArgb(255, 184, 33, 117);
        string json = NativeHostApiBridge.BuildConfigJson("zh-CN", AccentColorHelper.ToHex(accent),
            theme, material, opacity, intensity);
        var settings = PackageAppearance.Parse(json);
        Assert.Equal(theme == "Dark", settings.IsDark);
        Assert.Equal(accent, settings.Accent);
        Assert.Equal(material, settings.MaterialType);
        Assert.Equal(opacity, settings.MaterialOpacity);
        Assert.Equal(intensity, settings.MaterialIntensity);
    }

    [Fact]
    public void MalformedOrOlderHostPayloadKeepsSafeDefaults()
    {
        Assert.Equal(PackageAppearance.Default, PackageAppearance.Parse(null));
        Assert.Equal(PackageAppearance.Default, PackageAppearance.Parse("[1,2]"));
        Assert.Equal(PackageAppearance.Default, PackageAppearance.Parse("{broken"));
        var old = PackageAppearance.Parse("""{"locale":"zh-CN","accent":"#FF112233"}""");
        Assert.Equal(Color.FromArgb(255, 17, 34, 51), old.Accent);
        Assert.True(old.IsDark);
        var future = PackageAppearance.Parse("""{"theme":0,"accent":"bad","materialType":"future","materialOpacity":-1,"materialIntensity":1e100}""");
        Assert.Equal(PackageAppearance.Default.Accent, future.Accent);
        Assert.Equal("Mica", future.MaterialType);
        Assert.Equal(0, future.MaterialOpacity);
        Assert.Equal(1, future.MaterialIntensity);
    }

    [Fact]
    public void JsonWriterEscapesTextAndNormalizesNonfiniteNumbers()
    {
        string locale = "quote\"slash\\newline\n";
        string json = NativeHostApiBridge.BuildConfigJson(locale, "#123456",
            materialOpacity: double.NaN, materialIntensity: double.PositiveInfinity);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(locale, document.RootElement.GetProperty("locale").GetString());
        var parsed = PackageAppearance.Parse(json);
        Assert.Equal(0.8, parsed.MaterialOpacity);
        Assert.Equal(0.65, parsed.MaterialIntensity);
    }

    [Theory]
    [InlineData(true, true, 0.8, 0.65)]
    [InlineData(false, false, 0.3, 0.1)]
    [InlineData(true, false, 1, 1)]
    [InlineData(false, true, 0, 0)]
    public void PackageMaterialsMatchBuiltInRenderingFormulas(bool dark, bool alternate, double opacity, double intensity)
    {
        Color accent = Color.FromArgb(255, 185, 31, 95);
        Assert.Equal(WidgetMaterialVisualCalculator.BuildContentTintColor(dark, accent),
            PackageMaterials.BuildContentTintColor(dark, accent));
        Assert.Equal(WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(dark, accent, opacity),
            PackageMaterials.BuildContentSolidSurfaceColor(dark, accent, opacity));
        Assert.Equal(WidgetMaterialVisualCalculator.BuildEmbeddedMicaTintOverlayColor(dark, accent, alternate, intensity),
            PackageMaterials.BuildEmbeddedMicaTintOverlayColor(dark, accent, alternate, intensity));
        var expected = WidgetMaterialVisualCalculator.CalculateAcrylic(dark, alternate, opacity, intensity);
        var actual = PackageMaterials.CalculateAcrylic(dark, alternate, opacity, intensity);
        Assert.Equal(expected.TintOpacity, actual.TintOpacity);
        Assert.Equal(expected.LuminosityOpacity, actual.LuminosityOpacity);
        var palette = new GlanceImagePalette(accent, Color.FromArgb(255, 20, 180, 135));
        var gradient = WidgetMaterialVisualCalculator.BuildImagePaletteGradient(dark, palette);
        var packageGradient = PackageMaterials.BuildImagePaletteGradient(dark, new PackagePalette(palette.Primary, palette.Secondary));
        Assert.Equal(gradient.StartColor, packageGradient.StartColor);
        Assert.Equal(gradient.EndColor, packageGradient.EndColor);
    }

    [Fact]
    public void NativePaletteSamplesTheSameColorsAsBuiltIn()
    {
        byte[] pixels = [0, 0, 255, 255, 0, 0, 255, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 0, 0, 0];
        var expected = GlanceImagePaletteService.ExtractPalette(pixels)!.Value;
        var actual = PackagePaletteService.ExtractPalette(pixels)!.Value;
        Assert.Equal(expected.Primary, actual.Primary);
        Assert.Equal(expected.Secondary, actual.Secondary);
        Assert.Null(PackagePaletteService.ExtractPalette([0, 0, 0, 0]));
    }

    [Fact]
    public void MaterialPreferencesSurviveSnapshotReadAndNormalize()
    {
        string root = Directory.CreateTempSubdirectory("glance-material").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "glance-data.json"),
                """{"calendarMaterialMode":"FollowImage","calendarImageMaterialTransparency":0.61}""");
            var settings = PackageDataFile.Load(root)!.Settings;
            Assert.Equal("FollowImage", settings.CalendarMaterialMode.ToString());
            Assert.Equal(0.61, settings.CalendarImageMaterialTransparency);
            File.WriteAllText(Path.Combine(root, "glance-data.json"),
                """{"calendarMaterialMode":999,"calendarImageMaterialTransparency":3}""");
            settings = PackageDataFile.Load(root)!.Settings;
            Assert.Equal("FollowSystem", settings.CalendarMaterialMode.ToString());
            Assert.Equal(1, settings.CalendarImageMaterialTransparency);
        }
        finally { Directory.Delete(root, true); }
    }
}
