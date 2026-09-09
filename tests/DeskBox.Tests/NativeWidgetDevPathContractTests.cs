namespace DeskBox.Tests;

/// <summary>
/// Audit round 17 found the dev smoke chain broken (bootstrap installed with
/// Store policy, so the isDevelopment activation gate could never engage and
/// every dev run silently degraded to the raw DLL) and the raw-directory
/// fallback compiled unconditionally into Release. These ratchets pin both
/// fixes at the source level so they cannot regress quietly.
/// </summary>
public class NativeWidgetDevPathContractTests
{
    private static string PilotSource() => File.ReadAllText(TestPaths.SourceFile(
        "src/DeskBox/Services/Plugins/NativeWidgetPilot.cs"));

    private static string LoaderSource() => File.ReadAllText(TestPaths.SourceFile(
        "src/DeskBox/Services/Plugins/NativeWidgetPackageLoader.cs"));

    [Fact]
    public void RawDevFallbackStaysBehindThePilotGuard()
    {
        string source = PilotSource();
        int tryCreate = source.IndexOf("public static bool TryCreate", StringComparison.Ordinal);
        Assert.True(tryCreate >= 0, "TryCreate not found");
        string body = source[tryCreate..source.IndexOf("internal sealed class", tryCreate, StringComparison.Ordinal)];

        int guard = body.IndexOf("#if DESKBOX_NATIVE_DEV_PILOT", StringComparison.Ordinal);
        int fallback = body.IndexOf("TryGetDevelopmentPackageRoot", StringComparison.Ordinal);
        int guardEnd = body.IndexOf("#endif", StringComparison.Ordinal);
        Assert.True(guard >= 0, "TryCreate has no DESKBOX_NATIVE_DEV_PILOT guard");
        Assert.True(fallback > guard, "raw-directory fallback must sit inside the pilot guard");
        Assert.True(guardEnd > fallback, "raw-directory fallback must sit inside the pilot guard");
    }

    [Fact]
    public void DevBootstrapInstallsAsDevelopmentRecords()
    {
        string source = PilotSource();
        int install = source.IndexOf("DevManager.Install(", StringComparison.Ordinal);
        Assert.True(install >= 0, "DevManager.Install call not found");
        string call = source.Substring(install, Math.Min(300, source.Length - install));
        Assert.Contains("PluginPackageVerificationPolicy.Development", call);
        // The old shape (default Store policy) must not come back.
        Assert.DoesNotContain("DevManager.Install(packageRoot)", source);
    }

    [Fact]
    public void DevelopmentDescriptorIsPilotGated()
    {
        string source = LoaderSource();
        int descriptor = source.IndexOf("public static NativePackageDescriptor CreateDevelopmentDescriptor", StringComparison.Ordinal);
        Assert.True(descriptor >= 0, "CreateDevelopmentDescriptor not found");
        int guard = source.LastIndexOf("#if DESKBOX_NATIVE_DEV_PILOT", descriptor, StringComparison.Ordinal);
        int guardEnd = source.IndexOf("#endif", descriptor, StringComparison.Ordinal);
        Assert.True(guard >= 0, "CreateDevelopmentDescriptor must sit behind #if DESKBOX_NATIVE_DEV_PILOT");
        Assert.True(guardEnd > descriptor, "CreateDevelopmentDescriptor must sit behind #if DESKBOX_NATIVE_DEV_PILOT");
    }

    [Fact]
    public void DevPathValidatorStaysLoadFree()
    {
        // TryGetDevelopmentPackageRoot is intentionally compiled in Release
        // (pure path validation, unit-tested); it must never grow module
        // loading or runtime calls.
        string source = LoaderSource();
        int start = source.IndexOf("public static string? TryGetDevelopmentPackageRoot", StringComparison.Ordinal);
        Assert.True(start >= 0, "TryGetDevelopmentPackageRoot not found");
        string body = source[start..source.IndexOf("public static", start + 10, StringComparison.Ordinal)];
        Assert.DoesNotContain("NativeLibrary", body);
        Assert.DoesNotContain("TryCreateInstance", body);
        Assert.DoesNotContain("TryOpenSession", body);
    }
}
