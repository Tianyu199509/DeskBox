using DeskBox.Services.Plugins;

namespace DeskBox.Tests;

/// <summary>
/// HostApi v4 session attribution (audit round 20 section 31): every
/// HostApi table carries an opaque context id that the package echoes back
/// on config-subscription and instance-config writes. The registry resolves
/// the id to the owning package and dies at session shutdown - unknown or
/// forged ids resolve to nothing, and an instance-config write for an
/// instance the session does not own is rejected at the source level
/// (pinned by the ratchet below).
/// </summary>
public class NativeHostApiContextTests
{
    [Fact]
    public void RegisteredContextResolvesToItsPackage()
    {
        nint context = NativePackageContextRegistry.Register(
            new NativePackageContext { PackageId = "deskbox.test" });
        try
        {
            NativePackageContext? resolved = NativePackageContextRegistry.TryResolve(context);
            Assert.NotNull(resolved);
            Assert.Equal("deskbox.test", resolved!.PackageId);
        }
        finally
        {
            NativePackageContextRegistry.Unregister(context);
        }
        Assert.Null(NativePackageContextRegistry.TryResolve(context));
    }

    [Fact]
    public void UnknownContextResolvesToNothing()
    {
        Assert.Null(NativePackageContextRegistry.TryResolve((nint)12345));
    }

    [Fact]
    public void WriteThroughAttributionWiringStaysInPlace()
    {
        // The bridge must attribute the write to the echoed context and
        // reject instances the session does not own; the app must wire the
        // language-change source to the push.
        string loader = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/Plugins/NativeWidgetPackageLoader.cs"));
        Assert.Contains("NativePackageContextRegistry.TryResolve(context)", loader);
        Assert.Contains("string.Equals(registeredPackageId, owner.PackageId", loader);

        string app = File.ReadAllText(TestPaths.SourceFile("src/DeskBox/App.xaml.cs"));
        Assert.Contains("NativeHostApiBridge.PushConfigChanged", app);
    }

    [Fact]
    public void PackageSubscribesAndAppliesConfigChanges()
    {
        string exports = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox.GlancePackage/Abi/Exports.cs"));
        Assert.Contains("SetConfigChangedHandler", exports);
        Assert.Contains("OnConfigChanged", exports);
        Assert.Contains("RefreshAllForConfigChange", exports);

        string controller = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox.GlancePackage/Rendering/GlanceWidgetController.cs"));
        Assert.Contains("internal void ApplyConfigChange(CultureInfo culture)", controller);
    }
}
