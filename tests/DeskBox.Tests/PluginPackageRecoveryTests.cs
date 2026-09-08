using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Services.Plugins;

namespace DeskBox.Tests;

public sealed class PluginPackageRecoveryTests : IDisposable
{
    private const string Publisher = "1bc4f2db8438d2fd296bd48074088ccc726c265712125abbae975063ba719ea4";
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"))).FullName;
    private string Store => Path.Combine(_root, "store");
    private PluginPackageManager Manager() => new(Store, [Publisher]);

    private string Source()
    {
        string source = TestPaths.FromRepository("spikes/github-stats-live");
        string target = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        return target;
    }

    private static void ChangeManifest(string source, Action<JsonObject> change, bool sign = false)
    {
        string path = Path.Combine(source, "manifest.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(manifest);
        File.WriteAllText(path, manifest.ToJsonString());
        if (!sign) return;
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(TestPaths.FromRepository("scripts/spike/build-package.mjs"));
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(TestPaths.FromRepository("spikes/keys"));
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("fixture signing"); }
        Assert.True(process.ExitCode == 0, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }

    [Fact]
    public void Snapshot_IsUnaffectedByLaterSourceChanges()
    {
        string source = Source();
        string snapshot = Directory.CreateDirectory(Path.Combine(_root, "snapshot")).FullName;
        PluginPackageStorage.CopySnapshot(source, snapshot, PluginVerificationLimits.Default);
        File.AppendAllText(Path.Combine(source, "manifest.json"), "tamper");
        Assert.True(PluginPackageVerifier.Verify(snapshot).IsValid);
        Assert.False(PluginPackageVerifier.Verify(source).IsValid);
    }

    [Fact]
    public void CorruptInstalledContent_IsRejectedAndRepairedWithoutOverwritingLockedOldCopy()
    {
        var manager = Manager();
        string source = Source();
        PluginInstallResult first = manager.Install(source);
        Assert.True(first.Succeeded, string.Join("; ", first.Failures));
        string oldManifest = Path.Combine(first.InstallDirectory!, "manifest.json");
        File.SetAttributes(oldManifest, FileAttributes.Normal);
        File.AppendAllText(oldManifest, "tamper");
        Assert.Null(manager.Find(first.Package!.PackageId));
        using (File.Open(oldManifest, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            PluginInstallResult repaired = manager.Install(source);
            Assert.True(repaired.Succeeded, string.Join("; ", repaired.Failures));
            Assert.NotEqual(first.InstallDirectory, repaired.InstallDirectory);
            Assert.True(PluginPackageVerifier.Verify(repaired.InstallDirectory!).IsValid);
            Assert.NotNull(manager.Find(first.Package.PackageId));
            Assert.False(manager.Uninstall(first.Package.PackageId));
        }
        Assert.True(manager.Uninstall(first.Package.PackageId));
        Assert.False(Directory.Exists(Path.Combine(Store, first.Package.PackageId)));
    }

    [Theory]
    [InlineData("{ invalid")]
    [InlineData("[{}]")]
    [InlineData("null")]
    [InlineData("[{\"packageId\":null}]")]
    public void DamagedRegistry_BlocksInstallationAndRetainsEvidence(string damaged)
    {
        var manager = Manager();
        Assert.True(manager.Install(Source()).Succeeded);
        string registry = Path.Combine(Store, "installed.json");
        File.WriteAllText(registry, damaged);
        Assert.False(manager.GetInstalledState().IsHealthy);
        Assert.False(manager.Install(Source()).Succeeded);
        Assert.Equal(damaged, File.ReadAllText(registry));
    }

    [Fact]
    public void RegistryPathTraversal_CannotDeleteOutsideThePackageStore()
    {
        var manager = Manager();
        PluginInstallResult installed = manager.Install(Source());
        Assert.True(installed.Succeeded);
        string registry = Path.Combine(Store, "installed.json");
        JsonArray records = JsonNode.Parse(File.ReadAllText(registry))!.AsArray();
        records[0]!["installRelativePath"] = "../outside";
        File.WriteAllText(registry, records.ToJsonString());
        string outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        Assert.Null(manager.Find(installed.Package!.PackageId));
        Assert.False(manager.Uninstall(installed.Package.PackageId));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void Store_RequiresHostTrustSeparateFromAValidSignature()
    {
        string source = Source();
        Assert.True(PluginPackageVerifier.Verify(source).IsValid);
        var manager = new PluginPackageManager(Store);
        Assert.False(manager.Install(source).Succeeded);
        Assert.True(manager.Install(source, PluginPackageVerificationPolicy.Development).Succeeded);
    }

    [Fact]
    public void Grants_NeverCrossPublisherIdentityOrInheritLegacyFormat()
    {
        var grants = new PluginGrantStore(Store);
        grants.SetGrants("com.example.app", Publisher, "network.fetch", ["api.github.com"]);
        Assert.NotEmpty(grants.GetGrants("com.example.app", Publisher));
        Assert.Empty(grants.GetGrants("com.example.app", new string('b', 64)));
        File.WriteAllText(Path.Combine(Store, "grants.json"), """{"com.example.app":{"network.fetch":["api.github.com"]}}""");
        Assert.Empty(grants.GetGrants("com.example.app", Publisher));
    }

    [Fact]
    public void DamagedGrantShape_FailsClosed()
    {
        var grants = new PluginGrantStore(Store);
        File.WriteAllText(Path.Combine(Store, "grants.json"),
            """{"com.example.app":{"publisherFingerprint":"PUBLISHER","permissions":{"network.fetch":7}}}""".Replace("PUBLISHER", Publisher));
        Assert.Empty(grants.GetGrants("com.example.app", Publisher));
    }

    [Fact]
    public void StructuredPayloadAndRuntimeMetadata_SurviveTheVerificationDocument()
    {
        string source = Source();
        ChangeManifest(source, manifest =>
        {
            JsonObject contribution = manifest["contributions"]![0]!.AsObject();
            contribution["template"] = "list";
            contribution["payload"] = JsonNode.Parse("""{"version":1,"title":"Items","items":[{"title":"First","enabled":true,"count":7}]}""");
            contribution.Remove("bindings");
            contribution["defaultSize"] = JsonNode.Parse("""{"width":360,"height":240}""");
            contribution["activationEvents"] = JsonNode.Parse("""["onWidgetOpen"]""");
            manifest["permissions"]![0]!["required"] = false;
            manifest["data"] = JsonNode.Parse("""{"dataSchemaVersion":2,"settingsSchemaVersion":1}""");
            manifest["fallback"] = JsonNode.Parse("""{"missingTemplate":"placeholder"}""");
        }, sign: true);
        PluginInstallResult installed = Manager().Install(source);
        Assert.True(installed.Succeeded, string.Join("; ", installed.Failures));
        VerifiedPluginPackage package = installed.Package!;
        JsonElement first = package.Contributions[0].Payload.GetProperty("items")[0];
        Assert.Equal(7, first.GetProperty("count").GetInt32());
        Assert.True(first.GetProperty("enabled").GetBoolean());
        Assert.Equal(new VerifiedWidgetSize(360, 240), package.Contributions[0].DefaultSize);
        Assert.Contains("onWidgetOpen", package.Contributions[0].ActivationEvents);
        Assert.False(package.Permissions[0].Required);
        Assert.Equal(2, package.DataSchema.GetProperty("dataSchemaVersion").GetInt32());
        Assert.Equal("placeholder", package.Fallback.GetProperty("missingTemplate").GetString());
    }

    [Fact]
    public void HostVersionCompatibility_IsAnInstallGate()
    {
        string source = Source();
        ChangeManifest(source, m => m["hostApi"] = JsonNode.Parse("""{"min":"9.0.0","max":"9.1.0"}"""), sign: true);
        Assert.True(PluginPackageVerifier.Verify(source).IsValid);
        PluginInstallResult result = Manager().Install(source);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, f => f.Contains("hostApi", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("version", "999999999999999999999.0.0", "version")]
    [InlineData("defaultSize", "wide", "defaultSize")]
    [InlineData("path", "$.items[999999999999999999999999].name", "minimal JSON path")]
    public void InvalidProtocolValues_AreRejectedBeforeRuntime(string field, string value, string error)
    {
        string source = Source();
        ChangeManifest(source, m =>
        {
            if (field == "version") m["version"] = value;
            if (field == "defaultSize") m["contributions"]![0]!["defaultSize"] = value;
            if (field == "path") m["contributions"]![0]!["bindings"]!["value"]!["path"] = value;
        });
        var verification = PluginPackageVerifier.Verify(source);
        Assert.False(verification.IsValid);
        Assert.Contains(verification.Failures, f => f.Contains(error, StringComparison.Ordinal));
    }

    [Fact]
    public void DirectoryDepthAndEmptyDirectoryCount_AreBounded()
    {
        string source = Source();
        Directory.CreateDirectory(Path.Combine(source, "one", "two", "three"));
        var depth = PluginPackageVerifier.Verify(source, limits: new(MaxTreeDepth: 1));
        Assert.False(depth.IsValid);
        Assert.Contains(depth.Failures, f => f.Contains("tree-depth", StringComparison.Ordinal));
        var count = PluginPackageVerifier.Verify(source, limits: new(MaxDirectoryCount: 1));
        Assert.False(count.IsValid);
    }

    [Fact]
    public void TotalByteBudget_StopsBeforeIntegrityHashing()
    {
        string source = Source();
        File.WriteAllBytes(Path.Combine(source, "extra.bin"), new byte[8192]);
        var verification = PluginPackageVerifier.Verify(source, limits: new(MaxTotalExpandedBytes: 4096));
        Assert.False(verification.IsValid);
        Assert.Contains(verification.Failures, f => f.Contains("total-expanded", StringComparison.Ordinal));
        Assert.DoesNotContain(verification.Failures, f => f.Contains("integrity mismatch", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        PluginPackageStorage.DeleteDirectory(Path.GetDirectoryName(_root)!, _root);
    }
}
