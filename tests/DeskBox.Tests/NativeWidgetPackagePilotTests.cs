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
                DeskBox.Services.Plugins.NativeWidgetPackageLoader.PackageDllFileName), "stub");
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
    public void DataRootIsPackageScopedUnderNativePackages()
    {
        string dataRoot = DeskBox.Services.Plugins.NativeWidgetPackageLoader.ResolveDataRoot(
            @"C:\plugins\deskbox-glance-dev", @"D:\DeskBoxData\data");
        Assert.Equal(@"D:\DeskBoxData\data\native-packages\deskbox-glance-dev", dataRoot);
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
