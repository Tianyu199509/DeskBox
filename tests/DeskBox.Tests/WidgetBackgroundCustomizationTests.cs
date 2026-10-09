using DeskBox.Models;
using DeskBox.Services;
using Windows.UI;

namespace DeskBox.Tests;

public sealed class WidgetBackgroundCustomizationTests : IDisposable
{
    private readonly string _tempRoot;

    public WidgetBackgroundCustomizationTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            "widget-background",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData("background.png", true)]
    [InlineData("background.PNG", true)]
    [InlineData("background.svg", true)]
    [InlineData("background.gif", false)]
    [InlineData("title-icon.png", false)]
    [InlineData("folder/background.png", false)]
    [InlineData("", false)]
    public void ImageFileNameValidation_UsesBackgroundStem(string fileName, bool expected)
    {
        Assert.Equal(expected, IsValidFileName(fileName));
    }

    private static bool IsValidFileName(string fileName)
    {
        var config = new WidgetConfig();
        WidgetBackgroundCustomization.SetImageOverride(config, fileName);
        return WidgetBackgroundCustomization.GetImageFileNameOverride(config) is not null;
    }

    [Fact]
    public void SetImageAndOptions_RoundTrip()
    {
        var config = new WidgetConfig();
        WidgetBackgroundCustomization.SetImageOverride(config, "background.jpg");
        WidgetBackgroundCustomization.SetFitOverride(config, "contain");
        WidgetBackgroundCustomization.SetDimPercent(config, 60);

        Assert.Equal("background.jpg", WidgetBackgroundCustomization.GetImageFileNameOverride(config));
        Assert.Equal(WidgetBackgroundCustomization.FitContain, WidgetBackgroundCustomization.GetFitOverride(config));
        Assert.Equal(60, WidgetBackgroundCustomization.ResolveDimPercent(config));
        Assert.True(WidgetBackgroundCustomization.HasCustomBackground(config));
    }

    [Fact]
    public void FitFill_IsTheStoredDefaultAndContainedIsExplicit()
    {
        var config = new WidgetConfig();
        WidgetBackgroundCustomization.SetImageOverride(config, "background.png");

        WidgetBackgroundCustomization.SetFitOverride(config, "fill");
        Assert.Null(WidgetBackgroundCustomization.GetFitOverride(config));
        Assert.DoesNotContain(
            WidgetBackgroundCustomization.FitMetadataKey,
            config.Metadata.Keys);

        WidgetBackgroundCustomization.SetFitOverride(config, "CONTAIN");
        Assert.Equal(
            WidgetBackgroundCustomization.FitContain,
            WidgetBackgroundCustomization.GetFitOverride(config));
    }

    [Fact]
    public void Dim_ClampsAndDefaults()
    {
        var config = new WidgetConfig();
        Assert.Equal(
            WidgetBackgroundCustomization.DefaultDimPercent,
            WidgetBackgroundCustomization.ResolveDimPercent(config));

        WidgetBackgroundCustomization.SetDimPercent(config, 500);
        Assert.Equal(
            WidgetBackgroundCustomization.MaxDimPercent,
            WidgetBackgroundCustomization.ResolveDimPercent(config));

        WidgetBackgroundCustomization.SetDimPercent(config, -3);
        Assert.Equal(
            WidgetBackgroundCustomization.MinDimPercent,
            WidgetBackgroundCustomization.ResolveDimPercent(config));
    }

    [Fact]
    public void Clear_RemovesImageFitAndDim()
    {
        var config = new WidgetConfig();
        WidgetBackgroundCustomization.SetImageOverride(config, "background.png");
        WidgetBackgroundCustomization.SetFitOverride(config, "Contain");
        WidgetBackgroundCustomization.SetDimPercent(config, 80);

        WidgetBackgroundCustomization.Clear(config);

        Assert.False(WidgetBackgroundCustomization.HasCustomBackground(config));
        Assert.Null(WidgetBackgroundCustomization.GetFitOverride(config));
        Assert.Equal(
            WidgetBackgroundCustomization.DefaultDimPercent,
            WidgetBackgroundCustomization.ResolveDimPercent(config));
    }

    [Fact]
    public void SetColorOverride_RoundTripsAsOpaqueHex()
    {
        var config = new WidgetConfig();
        // An 8-digit input (alpha ignored) normalizes to the opaque #RRGGBB
        // storage shape shared with the per-widget foreground color.
        WidgetBackgroundCustomization.SetColorOverride(
            config,
            Color.FromArgb(0x80, 0xAA, 0x11, 0x22));

        Assert.Equal("#AA1122", WidgetBackgroundCustomization.GetColorOverride(config));
        Assert.True(WidgetBackgroundCustomization.HasCustomBackground(config));

        WidgetBackgroundCustomization.SetColorOverride(config, null);
        Assert.Null(WidgetBackgroundCustomization.GetColorOverride(config));
        Assert.False(WidgetBackgroundCustomization.HasCustomBackground(config));
    }

    [Fact]
    public void ImageAndColorAreMutuallyExclusive()
    {
        var config = new WidgetConfig();
        WidgetBackgroundCustomization.SetImageOverride(config, "background.png");
        WidgetBackgroundCustomization.SetFitOverride(config, "Contain");
        WidgetBackgroundCustomization.SetDimPercent(config, 60);

        WidgetBackgroundCustomization.SetColorOverride(
            config,
            Color.FromArgb(0xFF, 0x10, 0x20, 0x30));
        Assert.Null(WidgetBackgroundCustomization.GetImageFileNameOverride(config));
        Assert.Null(WidgetBackgroundCustomization.GetFitOverride(config));

        WidgetBackgroundCustomization.SetImageOverride(config, "background.jpg");
        Assert.Null(WidgetBackgroundCustomization.GetColorOverride(config));
    }

    [Fact]
    public void Clear_RemovesColorToo()
    {
        var config = new WidgetConfig();
        WidgetBackgroundCustomization.SetColorOverride(
            config,
            Color.FromArgb(0xFF, 0x10, 0x20, 0x30));

        WidgetBackgroundCustomization.Clear(config);

        Assert.False(WidgetBackgroundCustomization.HasCustomBackground(config));
    }

    [Fact]
    public void NormalizeOverrides_HealsInvalidAndShadowedColors()
    {
        var config = new WidgetConfig();
        config.Metadata[WidgetBackgroundCustomization.ColorMetadataKey] = "not-hex";
        Assert.True(WidgetBackgroundCustomization.NormalizeOverrides(config));
        Assert.Null(WidgetBackgroundCustomization.GetColorOverride(config));

        // A color stored under an image is unreachable (the image wins) and
        // must self-heal away, mirroring fit/dim noise cleanup.
        config.Metadata[WidgetBackgroundCustomization.ImageMetadataKey] = "background.png";
        config.Metadata[WidgetBackgroundCustomization.ColorMetadataKey] = "#FF0000";
        Assert.True(WidgetBackgroundCustomization.NormalizeOverrides(config));
        Assert.Null(WidgetBackgroundCustomization.GetColorOverride(config));
        Assert.NotNull(WidgetBackgroundCustomization.GetImageFileNameOverride(config));

        // Hand-edited 8-digit hex normalizes to the canonical opaque form.
        config.Metadata.Remove(WidgetBackgroundCustomization.ImageMetadataKey);
        config.Metadata[WidgetBackgroundCustomization.ColorMetadataKey] = "#80AA1122";
        Assert.True(WidgetBackgroundCustomization.NormalizeOverrides(config));
        Assert.Equal("#AA1122", WidgetBackgroundCustomization.GetColorOverride(config));
    }

    [Fact]
    public void NormalizeOverrides_DropsInvalidEntriesAndOrphanOptions()
    {
        var config = new WidgetConfig();
        config.Metadata[WidgetBackgroundCustomization.ImageMetadataKey] = "background.gif";
        config.Metadata[WidgetBackgroundCustomization.FitMetadataKey] = "Diagonal";
        config.Metadata[WidgetBackgroundCustomization.DimMetadataKey] = "not-a-number";

        Assert.True(WidgetBackgroundCustomization.NormalizeOverrides(config));
        Assert.Empty(config.Metadata);

        // Fit/dim without an image are noise and must not survive a load.
        config.Metadata[WidgetBackgroundCustomization.FitMetadataKey] = "Contain";
        config.Metadata[WidgetBackgroundCustomization.DimMetadataKey] = "50";
        Assert.True(WidgetBackgroundCustomization.NormalizeOverrides(config));
        Assert.Empty(config.Metadata);
    }

    [Fact]
    public async Task AssetStore_BackgroundCopyReplacesStaleExtensionFiles()
    {
        var store = new WidgetTitleIconAssetStore(_tempRoot);
        string png = Path.Combine(_tempRoot, "bg.png");
        string jpg = Path.Combine(_tempRoot, "bg.jpg");
        await File.WriteAllTextAsync(png, "png");
        await File.WriteAllTextAsync(jpg, "jpg");

        Assert.Equal(
            "background.jpg",
            WidgetTitleIconAssetStore.GetStoredBackgroundFileName(@"C:\pics\BG.JPG"));

        Assert.Equal(
            WidgetTitleIconAssetResult.Copied,
            await store.CopyBackgroundImageAsync("widget-1", png));
        Assert.Equal(
            WidgetTitleIconAssetResult.Copied,
            await store.CopyBackgroundImageAsync("widget-1", jpg));

        Assert.NotNull(store.ResolveBackgroundPath("widget-1", "background.jpg"));
        Assert.Null(store.ResolveBackgroundPath("widget-1", "background.png"));

        string assetDirectory = store.GetAssetDirectory("widget-1");
        Assert.Equal(["background.jpg"], Directory.GetFiles(assetDirectory).Select(Path.GetFileName));
    }

    // ── Wiring contracts ────────────────────────────────────────

    [Fact]
    public void TheWidgetMenu_ExposesTheBackgroundCustomizer()
    {
        Assert.Contains(
            "Widget.CustomBackground.MenuLabel",
            File.ReadAllText(TestPaths.FromRepository(
                "src/DeskBox/Views/ContentWidgetWindow.Commands.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShellHostsTheCustomBackgroundLayerInsideThePlate()
    {
        string xaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetShell.xaml"));
        Assert.Contains("CustomBackgroundImage", xaml, StringComparison.Ordinal);
        Assert.Contains("CustomBackgroundScrim", xaml, StringComparison.Ordinal);
        Assert.Contains("CustomBackgroundColorLayer", xaml, StringComparison.Ordinal);

        string code = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetShell.xaml.cs"));
        Assert.Contains("CustomBackgroundSourceProperty", code, StringComparison.Ordinal);
        Assert.Contains("CustomBackgroundColorHexProperty", code, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowPriorityChain_HonorsThePerWidgetColor()
    {
        // Image > per-widget color > global image modes > material: the
        // window-side resolver assigns the color only when no image layer
        // resolved.
        string window = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.TitleIconCustomization.cs"));
        Assert.Contains("GetColorOverride", window, StringComparison.Ordinal);
        Assert.Contains("CustomBackgroundColorHex", window, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsClearAll_CoversColorOnlyWidgets()
    {
        string coordinator = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/AppearanceSettingsCoordinator.cs"));
        Assert.Contains("GetColorOverride", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetRemoval_CleansPerWidgetAssets()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.cs"));
        Assert.Contains(
            "DeleteWidgetAssets",
            manager,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GlanceYieldsToShellBackground_AndSettingsNormalizeCoversBackground()
    {
        string glance = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml.cs"));
        Assert.Contains("SetShellCustomBackgroundActive", glance, StringComparison.Ordinal);
        Assert.Contains("_shellCustomBackgroundActive", glance, StringComparison.Ordinal);

        string window = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        Assert.Contains("SetShellCustomBackgroundActive", window, StringComparison.Ordinal);

        string settings = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/SettingsService.cs"));
        Assert.Contains(
            "WidgetBackgroundCustomization.NormalizeOverrides",
            settings,
            StringComparison.Ordinal);
    }
}
