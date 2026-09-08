using DeskBox.Services.Plugins;

namespace DeskBox.Tests;

/// <summary>
/// Batch D: proves a runtime:native package flows through the full B1
/// install → verify → handle pipeline. Uses the spike-built Glance package
/// fixture (assembled by scripts/spike/build-official-glance.ps1). Tests
/// silently pass when the fixture is absent (CI has no AOT toolchain).
/// </summary>
public class OfficialGlancePackageInstallTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("deskbox-official-glance-").FullName;
    private string PackageSource => TestPaths.FromRepository(
        ".artifacts/official-glance-package/package");
    private PluginPackageManager Manager => new(
        Path.Combine(_root, "data", "plugins"),
        ["1bc4f2db8438d2fd296bd48074088ccc726c265712125abbae975063ba719ea4"]);
    private bool FixtureAvailable => File.Exists(Path.Combine(PackageSource, "manifest.json"));

    [Fact]
    public void NativePackage_InstallsAndProduces_Handle()
    {
        if (!FixtureAvailable) return;
        PluginInstallResult result = Manager.Install(PackageSource);
        Assert.True(result.Succeeded, string.Join("; ", result.Failures));
        Assert.NotNull(result.Package);
        Assert.Equal("deskbox.glance", result.Package!.PackageId);
        Assert.Equal("native", result.Package.Runtime);
        Assert.Equal("package.dll", result.Package.EntryMain);
        Assert.NotNull(result.InstallDirectory);
        Assert.True(File.Exists(Path.Combine(result.InstallDirectory!, "package.dll")),
            "native DLL must be present in the install directory");
    }

    [Fact]
    public void NativePackage_HandleActivatesThroughRuntimeManager()
    {
        if (!FixtureAvailable) return;
        PluginInstallResult result = Manager.Install(PackageSource);
        Assert.True(result.Succeeded, string.Join("; ", result.Failures));
        NativeInstalledPackageHandle? handle = Manager.TryCreateNativeHandle("deskbox.glance");
        Assert.NotNull(handle);
        Assert.Equal("native", handle!.Record.Runtime);
        Assert.True(File.Exists(Path.Combine(handle.InstallRoot, "package.dll")));
    }

    [Fact]
    public void NativePackage_CannotInstallUnsigned()
    {
        if (!FixtureAvailable) return;
        string tampered = Path.Combine(_root, "tampered");
        Directory.CreateDirectory(tampered);
        foreach (string file in Directory.GetFiles(PackageSource))
        {
            if (Path.GetFileName(file) is "package.integrity" or "manifest.json") continue;
            File.Copy(file, Path.Combine(tampered, Path.GetFileName(file)));
        }
        string manifestJson = File.ReadAllText(Path.Combine(PackageSource, "manifest.json"));
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(manifestJson)!.AsObject();
        manifest.Remove("signature");
        File.WriteAllText(Path.Combine(tampered, "manifest.json"), manifest.ToJsonString());
        PluginInstallResult result = Manager.Install(tampered);
        Assert.False(result.Succeeded);
    }
}
