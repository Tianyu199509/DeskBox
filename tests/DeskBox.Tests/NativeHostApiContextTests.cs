using System.Runtime.InteropServices;
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
        Assert.Contains("PackageInstanceRegistry.TryResolveMigration(widgetId)", loader);

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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ConfigChangedCallback();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FailShutdownThunk();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OkDestroyThunk(nint handle);

    private static int _pushCount;
    private static void OnPush() => _pushCount++;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CdeclAction();

    private static readonly CdeclAction PushTarget = OnPush;

    [Fact]
    public void DetachSessionRemovesHandlerAndContext()
    {
        // Behavior test (audit round 21 — the normal-shutdown DetachSession
        // skip was invisible without exactly this sequence).
        var context = NativePackageContextRegistry.Register(
            new NativePackageContext { PackageId = "deskbox.detach.test" });
        nint handler = Marshal.GetFunctionPointerForDelegate(PushTarget);

        NativeHostApiBridge.SubscribeConfigChanged(context, handler);
        NativeHostApiBridge.PushConfigChanged();
        Assert.Equal(1, _pushCount);

        NativeHostApiBridge.DetachSession(context);
        NativeHostApiBridge.PushConfigChanged();
        Assert.Equal(1, _pushCount); // stale handler must NOT fire again
        Assert.Null(NativePackageContextRegistry.TryResolve(context));
        Assert.Equal(0, NativeHostApiBridge.RegisteredConfigChangedHandlerCount);
    }

    [Fact]
    public void FailedShutdownMarksPackageFaulted()
    {
        // Behavior test (audit round 21): a session whose shutdown reported
        // failure causes Shutdown() to return false — the trigger the
        // runtime manager uses for Faulted marking.
        var failStub = (FailShutdownThunk)(() => unchecked((int)0x8000FFFF));
        var okStub = (OkDestroyThunk)(_ => 0);

        var session = new NativePackageSession(
            new NativePackageIdentity("f".PadLeft(64, '0'), "deskbox.faulted"),
            packageRoot: "", packageDataRoot: "",
            activateExport: 0, createExport: 0,
            destroyExport: Marshal.GetFunctionPointerForDelegate(okStub),
            shutdownExport: Marshal.GetFunctionPointerForDelegate(failStub),
            widgetEventExport: 0);

        var lease = NativeWidgetLease.Create(session, 0x5678, null!, "faulted-instance");
        lease.TryRelease();
        Assert.False(session.Shutdown()); // stub returns non-zero → false
    }

    [Fact]
    public void FailedShutdownPreventsReactivation()
    {
        // Integration: verify the faulted-marking path end-to-end through
        // the runtime manager's release method.
        var identity = new NativePackageIdentity("f".PadLeft(64, '0'), "deskbox.faulted-integration");

        var failStub = (FailShutdownThunk)(() => unchecked((int)0x8000FFFF));
        var okStub = (OkDestroyThunk)(_ => 0);

        var session = new NativePackageSession(
            identity, "", "", 0, 0,
            Marshal.GetFunctionPointerForDelegate(okStub),
            Marshal.GetFunctionPointerForDelegate(failStub),
            0);

        var lease = NativeWidgetLease.Create(session, 0x99, null!, "test-inst");
        NativeWidgetRuntimeManager.Release(lease);

        Assert.True(NativeWidgetRuntimeManager.IsFaulted(identity.Key));
    }
}
