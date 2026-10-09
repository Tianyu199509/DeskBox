using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetGlobalBackgroundTests
{
    // ── Panorama mapping math ────────────────────────────────────

    [Fact]
    public void Panorama_MonitorFit_CoversTheMonitorAndCentersTheOverflow()
    {
        // 2000x1000 image on a 1000x1000 monitor: fill crops horizontally,
        // fitted 2000x1000 centered → x offset -500, y offset 0.
        (double offsetX, double offsetY, double width, double height) =
            WidgetBackgroundPanoramaCalculator.ComputeMonitorFit(
                imageWidth: 2000, imageHeight: 1000,
                monitorWidth: 1000, monitorHeight: 1000);

        Assert.Equal(-500, offsetX, 3);
        Assert.Equal(0, offsetY, 3);
        Assert.Equal(2000, width, 3);
        Assert.Equal(1000, height, 3);
    }

    [Fact]
    public void Panorama_MonitorFit_IsPurelyLocal_SameResultOnEveryMonitor()
    {
        // The per-monitor model must not depend on any other screen: a
        // 3840x2160 image fitted onto a 1920x1080 monitor yields the same
        // matrix whether that monitor sits alone or beside a 4K panel — the
        // property the union fit lost on mixed-DPI setups.
        (double offsetX, double offsetY, double width, double height) =
            WidgetBackgroundPanoramaCalculator.ComputeMonitorFit(
                imageWidth: 3840, imageHeight: 2160,
                monitorWidth: 1920, monitorHeight: 1080);

        Assert.Equal(0, offsetX, 3);
        Assert.Equal(0, offsetY, 3);
        Assert.Equal(1920, width, 3);
        Assert.Equal(1080, height, 3);

        // Portrait image on a landscape monitor fills by width and crops
        // height, centered.
        (offsetX, offsetY, width, height) = WidgetBackgroundPanoramaCalculator.ComputeMonitorFit(
            imageWidth: 1000, imageHeight: 2000,
            monitorWidth: 1000, monitorHeight: 1000);
        Assert.Equal(0, offsetX, 3);
        Assert.Equal(-500, offsetY, 3);
        Assert.Equal(1000, width, 3);
        Assert.Equal(2000, height, 3);
    }

    [Fact]
    public void Panorama_MonitorFit_InvalidInputs_ReturnMonitorUnscaled()
    {
        Assert.Equal(
            (0, 0, 800, 600),
            WidgetBackgroundPanoramaCalculator.ComputeMonitorFit(0, 1000, 800, 600));
    }

    [Fact]
    public void Panorama_WindowInsideCanvas_MapsItsExactSlice()
    {
        // 2000x1000 image over a 1000x1000 canvas: UniformToFill crops
        // horizontally, so fitted = 2000x1000 at x offset -500.
        Windows.Foundation.Rect? viewbox = WidgetBackgroundPanoramaCalculator.ComputeViewbox(
            imageWidth: 2000, imageHeight: 1000,
            canvasX: 0, canvasY: 0, canvasWidth: 1000, canvasHeight: 1000,
            windowX: 250, windowY: 500, windowWidth: 100, windowHeight: 100);

        Assert.NotNull(viewbox);
        // Window at screen x=250 → fitted x starts at -500 → image x = 750.
        Assert.Equal(750, viewbox!.Value.X, 3);
        Assert.Equal(500, viewbox.Value.Y, 3);
        Assert.Equal(100, viewbox.Value.Width, 3);
        Assert.Equal(100, viewbox.Value.Height, 3);
    }

    [Fact]
    public void Panorama_WindowHangingOffCanvas_ClampsToTheImage()
    {
        // 1000x1000 image on a 1000x1000 canvas; a window half off the
        // bottom samples only the visible half.
        Windows.Foundation.Rect? viewbox = WidgetBackgroundPanoramaCalculator.ComputeViewbox(
            imageWidth: 1000, imageHeight: 1000,
            canvasX: 0, canvasY: 0, canvasWidth: 1000, canvasHeight: 1000,
            windowX: 400, windowY: 800, windowWidth: 200, windowHeight: 400);

        Assert.NotNull(viewbox);
        Assert.Equal(400, viewbox!.Value.X, 3);
        Assert.Equal(800, viewbox.Value.Y, 3);
        Assert.Equal(200, viewbox.Value.Width, 3);
        Assert.Equal(200, viewbox.Value.Height, 3);
    }

    [Fact]
    public void Panorama_WindowOutsideTheFittedImage_ReturnsNull()
    {
        Assert.Null(WidgetBackgroundPanoramaCalculator.ComputeViewbox(
            imageWidth: 1000, imageHeight: 1000,
            canvasX: 0, canvasY: 0, canvasWidth: 1000, canvasHeight: 1000,
            windowX: 5000, windowY: 5000, windowWidth: 100, windowHeight: 100));
        Assert.Null(WidgetBackgroundPanoramaCalculator.ComputeViewbox(
            imageWidth: 0, imageHeight: 100,
            canvasX: 0, canvasY: 0, canvasWidth: 1000, canvasHeight: 1000,
            windowX: 0, windowY: 0, windowWidth: 100, windowHeight: 100));
    }

    [Fact]
    public void Panorama_PortraitImageOnWideCanvas_CoversAndCenters()
    {
        // 1000x2000 portrait over a 2000x1000 canvas: fitted 2000x4000 at
        // y offset -1500; a window at y=0..100 maps to image y 750..850.
        Windows.Foundation.Rect? viewbox = WidgetBackgroundPanoramaCalculator.ComputeViewbox(
            imageWidth: 1000, imageHeight: 2000,
            canvasX: 0, canvasY: 0, canvasWidth: 2000, canvasHeight: 1000,
            windowX: 1900, windowY: 0, windowWidth: 100, windowHeight: 100);

        Assert.NotNull(viewbox);
        Assert.Equal(950, viewbox!.Value.X, 3);
        Assert.Equal(750, viewbox.Value.Y, 3);
        Assert.Equal(50, viewbox.Value.Width, 3);
        Assert.Equal(50, viewbox.Value.Height, 3);
    }

    // ── Global settings normalization ────────────────────────────

    [Fact]
    public void NormalizeGlobal_CanonicalizesModeFitAndDim()
    {
        var settings = new AppSettings();
        settings.WidgetBackgroundMode = "panorama";
        settings.WidgetBackgroundUnifiedFit = "fill";
        settings.WidgetBackgroundDim = 500;

        Assert.True(WidgetBackgroundCustomization.NormalizeGlobal(settings));
        Assert.Equal(WidgetBackgroundModeKinds.Panorama, settings.WidgetBackgroundMode);
        Assert.Null(settings.WidgetBackgroundUnifiedFit);
        Assert.Equal(
            WidgetBackgroundCustomization.MaxDimPercent,
            settings.WidgetBackgroundDim);

        // Material is stored as null so untouched profiles stay byte-stable.
        settings.WidgetBackgroundMode = "garbage";
        Assert.True(WidgetBackgroundCustomization.NormalizeGlobal(settings));
        Assert.Null(settings.WidgetBackgroundMode);
    }

    [Fact]
    public void GlobalImageBackground_MaterialModeOrMissingFile_IsInactive()
    {
        var settings = new AppSettings();
        Assert.False(WidgetBackgroundCustomization.IsGlobalImageBackgroundActive(settings));

        settings.WidgetBackgroundMode = WidgetBackgroundModeKinds.UnifiedImage;
        settings.WidgetBackgroundUnifiedImage = "background.png";
        // No shared file exists in the test environment: self-heals to the
        // material instead of rendering a broken surface.
        Assert.False(WidgetBackgroundCustomization.IsGlobalImageBackgroundActive(settings));
    }

    [Fact]
    public void EffectiveDim_PerWidgetWinsThenGlobalThenDefault()
    {
        var settings = new AppSettings();
        var config = new WidgetConfig();
        Assert.Equal(
            WidgetBackgroundCustomization.DefaultDimPercent,
            WidgetBackgroundCustomization.ResolveEffectiveDimPercent(config, settings));

        settings.WidgetBackgroundDim = 60;
        Assert.Equal(60, WidgetBackgroundCustomization.ResolveEffectiveDimPercent(config, settings));

        WidgetBackgroundCustomization.SetDimPercent(config, 20);
        Assert.Equal(20, WidgetBackgroundCustomization.ResolveEffectiveDimPercent(config, settings));
    }

    // ── Shared asset store ───────────────────────────────────────

    [Fact]
    public async Task SharedAssetStore_CopiesUnifiedAndPanoramaSideBySide()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            "global-background",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WidgetTitleIconAssetStore(root);
            string source = Path.Combine(root, "src.png");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(source, "img");

            Assert.Equal(
                WidgetTitleIconAssetResult.Copied,
                await store.CopyUnifiedBackgroundImageAsync(source));
            Assert.Equal(
                WidgetTitleIconAssetResult.Copied,
                await store.CopyPanoramaBackgroundImageAsync(source));

            Assert.NotNull(store.ResolveUnifiedBackgroundPath("background.png"));
            Assert.NotNull(store.ResolvePanoramaBackgroundPath("panorama.png"));
            Assert.Equal(
                ["background.png", "panorama.png"],
                Directory.GetFiles(store.GetSharedDirectory())
                    .Select(Path.GetFileName)
                    .Order());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // ── Wiring contracts ─────────────────────────────────────────

    [Fact]
    public void GlobalBackground_IsWiredAcrossSchemaSettingsAndShell()
    {
        Assert.Contains(
            "CurrentSchemaVersion = 13",
            File.ReadAllText(TestPaths.FromRepository(
                "src/DeskBox/Services/SettingsMigrationService.cs")),
            StringComparison.Ordinal);
        Assert.Contains(
            "Migration_9_To_10",
            File.ReadAllText(TestPaths.FromRepository(
                "src/DeskBox/Services/SettingsMigrationService.cs")),
            StringComparison.Ordinal);

        string settingsXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsWindow.xaml"));
        Assert.Contains("{Binding WidgetBackgroundMode, Mode=TwoWay}", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("{Binding WidgetBackgroundDimPercent, Mode=TwoWay}", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ChooseWidgetBackgroundImage_Click", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ClearPerWidgetBackgroundsButton_Click", settingsXaml, StringComparison.Ordinal);

        string bridge = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Features/Appearance/AppearanceSettingsViewModel.AotBindableProperties.cs"));
        Assert.Contains("nameof(WidgetBackgroundMode)", bridge, StringComparison.Ordinal);
        Assert.Contains("nameof(WidgetBackgroundDimPercent)", bridge, StringComparison.Ordinal);

        string windowBase = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.TitleIconCustomization.cs"));
        Assert.Contains("ResolveCustomPanoramaSource", windowBase, StringComparison.Ordinal);
        Assert.Contains("UpdateCustomPanoramaViewport", windowBase, StringComparison.Ordinal);

        string bounds = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        Assert.Contains("UpdateCustomPanoramaViewport", bounds, StringComparison.Ordinal);

        // The panorama clip must track the host element, not the shell: the
        // host flips Collapsed→Visible without a shell resize, and a zero
        // RenderSize at that moment clips the image away forever.
        string shellCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        Assert.Contains(
            "CustomBackgroundPanoramaHost.SizeChanged",
            shellCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsSearchCatalog_CoversTheWidgetBackgroundCard()
    {
        string catalog = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/SettingsSearchCatalog.cs"));
        Assert.Contains("Settings.WidgetBackground.Title", catalog, StringComparison.Ordinal);
        Assert.Contains("Settings.WidgetBackground.Image.Title", catalog, StringComparison.Ordinal);
        Assert.Contains("Settings.WidgetBackground.PerWidget.Title", catalog, StringComparison.Ordinal);
    }
}
