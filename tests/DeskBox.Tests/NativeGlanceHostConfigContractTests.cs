namespace DeskBox.Tests;

/// <summary>
/// D3 product-migration ratchets: the native glance package must run on the
/// host-provided locale and the current month, not on the probe-era fixed
/// values (zh-CN / September 2026 / 440x560 forever). The package project is
/// not referenced by tests, so these are source-level pins.
/// </summary>
public class NativeGlanceHostConfigContractTests
{
    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.SourceFile(relativePath));

    [Fact]
    public void PipelineDropsProbeEraFixedValues()
    {
        string pipeline = Read("src/DeskBox.GlancePackage/Rendering/GlanceMonthPipeline.cs");
        Assert.DoesNotContain("PinnedYear", pipeline);
        Assert.DoesNotContain("PinnedMonth", pipeline);
        Assert.DoesNotContain("2026", pipeline);
        // No fixed culture and no locale-specific literal date format.
        Assert.DoesNotContain("\"zh-CN\"", pipeline);
        Assert.DoesNotContain("M月d日", pipeline);
        // The month must be derived from today, and culture/size must be
        // parameters all the way through Build and CreatePresentation.
        Assert.Contains("DateTime.Today", pipeline);
        Assert.Contains("CultureInfo culture", pipeline);
        Assert.Contains("double availableWidth", pipeline);
        Assert.Contains("double availableHeight", pipeline);
    }

    [Fact]
    public void ExportsWiresTheHostConfigChannel()
    {
        string exports = Read("src/DeskBox.GlancePackage/Abi/Exports.cs");
        Assert.Contains("HostConfig.Initialize(hostApi->GetConfigJson)", exports);
        string hostConfig = Read("src/DeskBox.GlancePackage/Services/HostConfig.cs");
        Assert.Contains("\"locale\"", hostConfig);
        // Graceful degradation: a failed fetch must return null, never throw
        // across the config path.
        Assert.Contains("return null", hostConfig);
    }

    [Fact]
    public void ViewportChangesReachTheResponsiveRebuild()
    {
        string handle = Read("src/DeskBox.GlancePackage/Rendering/GlanceWidgetHandle.cs");
        Assert.Contains("_controller.OnViewportChanged(width, height)", handle);
        string controller = Read("src/DeskBox.GlancePackage/Rendering/GlanceWidgetController.cs");
        // Debounced rebuild on resize, not a rebuild per event.
        Assert.Contains("resizeTimer", controller);
        // Menu strings go through the locale table, with zh-CN kept only as
        // the last-resort inline fallback.
        Assert.Contains("PackageStrings.Get(\"menuNextBackground\"", controller);
        // Lifecycle events drive real work: the clock timer exists and the
        // energy policy decides whether timers run (audit rounds 18-19).
        Assert.Contains("_clockTimer", controller);
        Assert.Contains("GlanceLifecyclePolicy.Compute", controller);
        Assert.Contains("public void Dispose()", controller);
    }

    [Fact]
    public void LocaleStringTablesShipBothLanguages()
    {
        string zh = Read("src/DeskBox.GlancePackage/strings/zh-CN.json");
        string en = Read("src/DeskBox.GlancePackage/strings/en-US.json");
        foreach (string key in new[] { "menuNextBackground", "menuPauseRotation", "menuSettings" })
        {
            Assert.Contains($"\"{key}\"", zh);
            Assert.Contains($"\"{key}\"", en);
        }
    }
}
