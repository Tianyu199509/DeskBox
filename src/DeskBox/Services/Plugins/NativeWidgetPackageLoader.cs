using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Stable package identity for the native runtime (batch C1). Data roots are
/// keyed by publisher fingerprint + package id - NEVER by install directory
/// names, whose leaf segments are content hashes that change on every update.
/// </summary>
internal readonly record struct NativePackageIdentity(string PublisherFingerprint, string PackageId)
{
    public string Key => $"{PublisherFingerprint}/{PackageId}";

    public string ResolvePackageDataRoot(string dataDirectory) =>
        Path.Combine(dataDirectory, "packages", PublisherFingerprint, PackageId);

    public string ResolveInstanceDataRoot(string dataDirectory, string instanceId) =>
        Path.Combine(ResolvePackageDataRoot(dataDirectory), "instances", instanceId);
}

/// <summary>Everything needed to activate a native package at a concrete location.</summary>
internal sealed record NativePackageDescriptor(
    string PublisherFingerprint,
    string PackageId,
    string PackageRoot)
{
    public NativePackageIdentity Identity => new(PublisherFingerprint, PackageId);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeHostApiV1
{
    public nint Log;
}

/// <summary>Host-side callbacks exposed to native packages via the HostApi table.</summary>
internal static unsafe class NativeHostApiBridge
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void Log(byte* utf8, int length)
    {
        try
        {
            App.LogVerbose("[NativePackage:guest] " + Encoding.UTF8.GetString(utf8, length));
        }
        catch
        {
            // Never fail a package->host log callback.
        }
    }
}

/// <summary>
/// Batch C1 runtime contract (ABI v2): one session per package identity,
/// activated exactly once; widget instances are created per (contribution,
/// instance) pair and destroyed by opaque handle; the last destroy shuts the
/// package down. NativeAOT modules stay loaded for process lifetime by design.
/// UI thread only.
/// </summary>
internal static class NativeWidgetRuntimeManager
{
    public const int RequiredAbiVersion = 2;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, NativePackageSession> Sessions = [];

    public static bool TryCreateInstance(
        NativePackageDescriptor descriptor,
        string contributionId,
        string instanceId,
        string dataDirectory,
        out NativeWidgetLease? lease)
    {
        lease = null;
        NativePackageSession session;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(descriptor.Identity.Key, out session!))
            {
                session = NativeWidgetPackageLoader.TryOpenSession(descriptor, dataDirectory);
                if (session is null) return false;
                Sessions[descriptor.Identity.Key] = session;
            }
        }
        NativeWidgetLease? created = session.CreateInstance(contributionId, instanceId);
        if (created is null) return false;
        lease = created;
        return true;
    }

    /// <summary>
    /// Product entry point: only a B1-verified installed package may activate.
    /// EntryMain marks a native package; the runtime-type schema extension for
    /// native packages lands with the batch C package-format freeze.
    /// </summary>
    public static bool TryCreateFromVerified(
        VerifiedPluginPackage package,
        string installDirectory,
        string contributionId,
        string instanceId,
        string dataDirectory,
        out NativeWidgetLease? lease)
    {
        lease = null;
        if (string.IsNullOrEmpty(package.EntryMain))
        {
            App.LogVerbose("[NativePackage] verified package has no native entry; refusing to activate");
            return false;
        }
        return TryCreateInstance(
            new NativePackageDescriptor(package.PublisherFingerprint, package.PackageId, installDirectory),
            contributionId,
            instanceId,
            dataDirectory,
            out lease);
    }

    internal static void Release(NativeWidgetLease lease)
    {
        lock (Gate)
        {
            if (!lease.TryRelease()) return;
            if (lease.Session.LiveInstanceCount == 0)
            {
                lease.Session.Shutdown();
                Sessions.Remove(lease.Session.Identity.Key);
            }
        }
    }
}

/// <summary>One live widget instance; Dispose routes to the manager's release path.</summary>
internal sealed class NativeWidgetLease : IDisposable
{
    private NativePackageSession _session = null!;
    private nint _handle;
    private bool _released;

    internal Microsoft.UI.Xaml.FrameworkElement View { get; private set; } = null!;

    internal NativePackageSession Session => _session;

    internal static NativeWidgetLease Create(NativePackageSession session, nint handle, Microsoft.UI.Xaml.FrameworkElement view) => new()
    {
        _session = session,
        _handle = handle,
        View = view,
    };

    /// <summary>Idempotent release; returns true when this call performed the destroy.</summary>
    internal bool TryRelease()
    {
        if (_released) return false;
        _released = true;
        _session.DestroyWidget(_handle);
        return true;
    }

    void IDisposable.Dispose() => NativeWidgetRuntimeManager.Release(this);
}

internal sealed unsafe class NativePackageSession
{
    private readonly nint _activateExport;
    private readonly nint _createExport;
    private readonly nint _destroyExport;
    private readonly nint _shutdownExport;
    private readonly HashSet<nint> _liveHandles = [];

    internal NativePackageSession(
        NativePackageIdentity identity,
        string packageRoot,
        string packageDataRoot,
        nint activateExport,
        nint createExport,
        nint destroyExport,
        nint shutdownExport)
    {
        Identity = identity;
        PackageRoot = packageRoot;
        PackageDataRoot = packageDataRoot;
        _activateExport = activateExport;
        _createExport = createExport;
        _destroyExport = destroyExport;
        _shutdownExport = shutdownExport;
    }

    internal NativePackageIdentity Identity { get; }
    internal string PackageRoot { get; }
    internal string PackageDataRoot { get; }
    internal int LiveInstanceCount { get { lock (_liveHandles) return _liveHandles.Count; } }

    internal static void Activate(NativePackageSession session)
    {
        var activate = (delegate* unmanaged[Cdecl]<char*, int, char*, int, NativeHostApiV1*, int>)session._activateExport;
        var hostApi = new NativeHostApiV1
        {
            Log = (nint)(delegate* unmanaged[Cdecl]<byte*, int, void>)&NativeHostApiBridge.Log,
        };
        int status;
        fixed (char* package = session.PackageRoot)
        fixed (char* data = session.PackageDataRoot)
        {
            NativeHostApiV1* api = &hostApi;
            status = activate(package, session.PackageRoot.Length, data, session.PackageDataRoot.Length, api);
        }
        if (status != 0)
        {
            throw new InvalidOperationException($"[NativePackage] activate failed for {session.Identity.Key}: 0x{status:X8}");
        }
        App.Log($"[NativePackage] session active: {session.Identity.Key}");
    }

    internal NativeWidgetLease? CreateInstance(string contributionId, string instanceId)
    {
        string instanceDataRoot = Path.Combine(PackageDataRoot, "instances", instanceId);
        var create = (delegate* unmanaged[Cdecl]<char*, int, char*, int, char*, int, nint*, nint*, int>)_createExport;
        nint handle = 0, viewAbi = 0;
        int status;
        fixed (char* contribution = contributionId)
        fixed (char* instance = instanceId)
        fixed (char* dataRoot = instanceDataRoot)
        {
            status = create(contribution, contributionId.Length, instance, instanceId.Length, dataRoot, instanceDataRoot.Length, &handle, &viewAbi);
        }
        if (status != 0 || handle == 0 || viewAbi == 0)
        {
            App.Log($"[NativePackage] create {contributionId}/{instanceId} failed: 0x{status:X8}");
            return null;
        }
        Microsoft.UI.Xaml.FrameworkElement view;
        try
        {
            view = WinRT.MarshalInspectable<Microsoft.UI.Xaml.FrameworkElement>.FromAbi(viewAbi);
            view.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
            view.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
        }
        catch (Exception error)
        {
            WinRT.MarshalInspectable<Microsoft.UI.Xaml.FrameworkElement>.DisposeAbi(viewAbi);
            App.Log($"[NativePackage] view projection failed: {error.Message}");
            return null;
        }
        finally
        {
            WinRT.MarshalInspectable<Microsoft.UI.Xaml.FrameworkElement>.DisposeAbi(viewAbi);
        }
        lock (_liveHandles) _liveHandles.Add(handle);
        return NativeWidgetLease.Create(this, handle, view);
    }

    internal void DestroyWidget(nint handle)
    {
        lock (_liveHandles) _liveHandles.Remove(handle);
        ((delegate* unmanaged[Cdecl]<nint, int>)_destroyExport)(handle);
    }

    internal void Shutdown()
    {
        try
        {
            ((delegate* unmanaged[Cdecl]<int>)_shutdownExport)();
            App.Log($"[NativePackage] session shut down: {Identity.Key}");
        }
        catch (Exception error)
        {
            App.Log($"[NativePackage] shutdown failed for {Identity.Key}: {error.Message}");
        }
    }
}

/// <summary>Module loading + ABI resolution for the runtime manager (ABI v2).</summary>
internal static class NativeWidgetPackageLoader
{
    public const string DevelopmentPackageEnvironmentVariable = "DESKBOX_DEV_NATIVE_GLANCE";
    public const string PackageDllFileName = "DeskBox.Glance.NativePackage.dll";
    public const string DevelopmentPublisherFingerprint = "dev-pilot";

    /// <summary>Path validation only - no module loading (unit-testable).</summary>
    public static string? TryGetDevelopmentPackageRoot()
    {
        string? configured = Environment.GetEnvironmentVariable(DevelopmentPackageEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured)) return null;
        try
        {
            string root = Path.GetFullPath(configured.Trim());
            if (!Directory.Exists(root)) return null;
            return File.Exists(Path.Combine(root, PackageDllFileName)) ? root : null;
        }
        catch
        {
            return null;
        }
    }

    internal static unsafe NativePackageSession? TryOpenSession(NativePackageDescriptor descriptor, string dataDirectory)
    {
        try
        {
            string packageDataRoot = descriptor.Identity.ResolvePackageDataRoot(dataDirectory);
            Directory.CreateDirectory(packageDataRoot);
            nint module = NativeLibrary.Load(Path.Combine(descriptor.PackageRoot, PackageDllFileName));
            if (!TryGetExport(module, "deskbox_package_get_abi_version", out nint versionExport) ||
                !TryGetExport(module, "deskbox_package_activate", out nint activateExport) ||
                !TryGetExport(module, "deskbox_widget_create", out nint createExport) ||
                !TryGetExport(module, "deskbox_widget_destroy", out nint destroyExport) ||
                !TryGetExport(module, "deskbox_package_shutdown", out nint shutdownExport))
            {
                App.LogVerbose("[NativePackage] unified ABI v2 exports missing");
                return null;
            }
            int version = ((delegate* unmanaged[Cdecl]<int>)versionExport)();
            if (version != NativeWidgetRuntimeManager.RequiredAbiVersion)
            {
                App.Log($"[NativePackage] ABI version {version} != {NativeWidgetRuntimeManager.RequiredAbiVersion}");
                return null;
            }
            var session = new NativePackageSession(
                descriptor.Identity, descriptor.PackageRoot, packageDataRoot,
                activateExport, createExport, destroyExport, shutdownExport);
            NativePackageSession.Activate(session);
            return session;
        }
        catch (Exception error)
        {
            App.Log($"[NativePackage] session open failed for {descriptor.Identity.Key}: {error.Message}");
            return null;
        }
    }

    private static bool TryGetExport(nint module, string name, out nint export)
    {
        try
        {
            export = NativeLibrary.GetExport(module, name);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            export = 0;
            return false;
        }
    }
}
