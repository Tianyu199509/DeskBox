namespace DeskBox.Tests;

public class NativeWidgetPackagePilotTests
{
    [Fact]
    public void DevelopmentRootRequiresEnvironmentAndDll()
    {
        string? original = Environment.GetEnvironmentVariable(
            DeskBox.Services.Plugins.NativeWidgetPackageLoader.DevelopmentPackageEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                DeskBox.Services.Plugins.NativeWidgetPackageLoader.DevelopmentPackageEnvironmentVariable, null);
            Assert.Null(DeskBox.Services.Plugins.NativeWidgetPackageLoader.TryGetDevelopmentPackageRoot());

            string emptyDir = Directory.CreateTempSubdirectory("deskbox-native-pilot-empty").FullName;
            Environment.SetEnvironmentVariable(
                DeskBox.Services.Plugins.NativeWidgetPackageLoader.DevelopmentPackageEnvironmentVariable, emptyDir);
            Assert.Null(DeskBox.Services.Plugins.NativeWidgetPackageLoader.TryGetDevelopmentPackageRoot());

            string packageDir = Directory.CreateTempSubdirectory("deskbox-native-pilot-pkg").FullName;
            File.WriteAllText(Path.Combine(packageDir,
                DeskBox.Services.Plugins.NativeWidgetPackageLoader.DevelopmentPackageDllFileName), "stub");
            Environment.SetEnvironmentVariable(
                DeskBox.Services.Plugins.NativeWidgetPackageLoader.DevelopmentPackageEnvironmentVariable, packageDir);
            Assert.Equal(
                Path.GetFullPath(packageDir),
                DeskBox.Services.Plugins.NativeWidgetPackageLoader.TryGetDevelopmentPackageRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DeskBox.Services.Plugins.NativeWidgetPackageLoader.DevelopmentPackageEnvironmentVariable, original);
        }
    }

    [Fact]
    public void PackageDataRootIsStableAcrossInstallPathChanges()
    {
        // Batch C1 contract: roots key on publisher+packageId, never on install
        // directory names (B1 leaf dirs are content hashes that rotate on update).
        var identity = new DeskBox.Services.Plugins.NativePackageIdentity(
            "a".PadLeft(64, '0'), "com.deskbox.glance");
        string v1 = identity.ResolvePackageDataRoot(@"D:\Data\data");
        string v2 = identity.ResolvePackageDataRoot(@"D:\Data\data");
        Assert.Equal(v1, v2);
        Assert.Equal(
            Path.Combine(@"D:\Data\data", "packages", "a".PadLeft(64, '0'), "com.deskbox.glance"),
            v1);

        // Different publisher fingerprints (takeover guard) never share a root.
        var other = new DeskBox.Services.Plugins.NativePackageIdentity(
            "b".PadLeft(64, '0'), "com.deskbox.glance");
        Assert.NotEqual(v1, other.ResolvePackageDataRoot(@"D:\Data\data"));
    }

    [Fact]
    public void InstanceStorageKeysAreHashedAndTraversalProof()
    {
        var identity = new DeskBox.Services.Plugins.NativePackageIdentity(
            "a".PadLeft(64, '0'), "com.deskbox.glance");
        // Persisted instance ids are input: raw ids never become path fragments.
        string normal = DeskBox.Services.Plugins.NativePackageIdentity.InstanceStorageKey("widget-42");
        string hostile = DeskBox.Services.Plugins.NativePackageIdentity.InstanceStorageKey("../../escape");
        string rooted = DeskBox.Services.Plugins.NativePackageIdentity.InstanceStorageKey(@"C:\steal");
        Assert.Matches("^[0-9a-f]{64}$", normal);
        Assert.Matches("^[0-9a-f]{64}$", hostile);
        Assert.Matches("^[0-9a-f]{64}$", rooted);
        Assert.Equal(normal, DeskBox.Services.Plugins.NativePackageIdentity.InstanceStorageKey("widget-42"));
        Assert.NotEqual(normal, hostile);
        string root = identity.ResolveInstanceDataRoot(@"D:\Data\data", "../..");
        Assert.DoesNotContain("..", root);
        Assert.StartsWith(identity.ResolvePackageDataRoot(@"D:\Data\data"), root);
    }

    [Fact]
    public void PilotContentMustBeDisposableForHostLifecycle()
    {
        // The host disposes widget content via `is IDisposable` only; a plain
        // Dispose() method without the interface silently skips native destroy
        // (regression seen in batch C1, caught by audit round 12).
        Type content = typeof(DeskBox.Services.Plugins.NativeWidgetPilot)
            .Assembly.GetType("DeskBox.Services.Plugins.NativeWidgetPilotContent")!;
        Assert.True(typeof(IDisposable).IsAssignableFrom(content));
        Assert.True(typeof(DeskBox.Contracts.IWidgetContent).IsAssignableFrom(content));
    }

    [Fact]
    public void FactoryKeepsBuiltInFallbackBehindPilotSeam()
    {
        string factory = File.ReadAllText(TestPaths.SourceFile("src/DeskBox/Services/WidgetContentFactory.cs"));
        Assert.Contains("NativeWidgetPilot.TryCreate", factory);
        Assert.Contains("provider.CreateDetachedContent(config, context)", factory);

        // The pilot must stay out of feature-owned sources (ratchet: feature
        // files may not depend on host plugin namespaces) and off the frozen
        // Glance file inventory (no feature tokens in pilot file names).
        string provider = File.ReadAllText(TestPaths.SourceFile("src/DeskBox/Services/GlanceWidgetContentProvider.cs"));
        Assert.DoesNotContain("NativeWidgetPilot", provider);
        Assert.DoesNotContain("Services.Plugins", provider);
    }

    [Fact]
    public void PilotFilesStayOffTheFrozenJsonBaseline()
    {
        foreach (string relativePath in new[]
        {
            "src/DeskBox/Services/Plugins/NativeWidgetPackageLoader.cs",
            "src/DeskBox/Services/Plugins/NativeWidgetPilot.cs",
        })
        {
            string source = File.ReadAllText(TestPaths.SourceFile(relativePath));
            Assert.DoesNotContain("JsonSerializer", source);
            Assert.DoesNotContain("Glance", Path.GetFileName(relativePath));
        }
    }
}
