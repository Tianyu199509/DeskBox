using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Stable package identity for the native runtime (batch C1). Data roots are
/// keyed by publisher fingerprint + package id - NEVER by install directory
/// names, whose leaf segments are content hashes that change on every update.
/// Instance directories use hashed storage keys: persisted instance ids are
/// input and must never be treated as safe path fragments.
/// </summary>
internal readonly record struct NativePackageIdentity(string PublisherFingerprint, string PackageId)
{
    public string Key => $"{PublisherFingerprint}/{PackageId}";

    public string ResolvePackageDataRoot(string dataDirectory) =>
        Path.Combine(dataDirectory, "packages", PublisherFingerprint, PackageId);

    public static string InstanceStorageKey(string instanceId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(instanceId));
        return Convert.ToHexStringLower(hash);
    }

    public string ResolveInstanceDataRoot(string dataDirectory, string instanceId) =>
        Path.Combine(ResolvePackageDataRoot(dataDirectory), "instances", InstanceStorageKey(instanceId));
}

/// <summary>Everything needed to activate a native package at a concrete location.</summary>
internal sealed record NativePackageDescriptor(
    string PublisherFingerprint,
    string PackageId,
    string ContentHash,
    string PackageRoot,
    string EntryModuleFileName)
{
    public NativePackageIdentity Identity => new(PublisherFingerprint, PackageId);
}

/// <summary>
/// Structurally paired installed-package handle: the record and its resolved
/// install root travel together and can only be produced through
/// PluginPackageManager.TryCreateNativeHandle - the runtime never accepts a
/// separable "verified package + arbitrary path" pair.
/// </summary>
public sealed record NativeInstalledPackageHandle
{
    internal InstalledPackageRecord Record { get; }
    internal string InstallRoot { get; }

    internal NativeInstalledPackageHandle(InstalledPackageRecord record, string installRoot)
    {
        Record = record;
        InstallRoot = installRoot;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeHostApiV1
{
    public uint Size;
    public uint Version;
    public nint Log;
    public nint GetConfigJson;
    public nint SetConfigChangedHandler;
}

/// <summary>Host-side callbacks exposed to native packages via the HostApi table.</summary>
internal static unsafe class NativeHostApiBridge
{
    internal const uint CurrentVersion = 2;

    internal static NativeHostApiV1 Create() => new()
    {
        Size = (uint)sizeof(NativeHostApiV1),
        Version = CurrentVersion,
        Log = (nint)(delegate* unmanaged[Cdecl]<byte*, int, void>)&Log,
        GetConfigJson = (nint)(delegate* unmanaged[Cdecl]<byte*, int, int>)&GetConfigJson,
        SetConfigChangedHandler = (nint)(delegate* unmanaged[Cdecl]<nint, int>)&SetConfigChangedHandler,
    };

    /// <summary>Config payload: locale + accent theme tokens (batch C2 contract).</summary>
    internal static string BuildConfigJson(string locale, string accent) =>
        $$"""{"locale":"{{locale}}","accent":"{{accent}}"}""";

    private static readonly string CurrentConfig = BuildConfigJson(
        System.Globalization.CultureInfo.CurrentUICulture.Name,
        "#FF4CC2FF");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetConfigJson(byte* buffer, int bufferLength)
    {
        try
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(CurrentConfig);
            if (utf8.Length > bufferLength) return utf8.Length;
            for (int index = 0; index < utf8.Length; index++) buffer[index] = utf8[index];
            return utf8.Length;
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetConfigChangedHandler(nint handler)
    {
        // Package-side subscription recorded; the product notification source
        // (theme/locale change events) wires up during batch D migration.
        App.LogVerbose($"[NativePackage] config-changed handler registered: 0x{handler:X}");
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Log(byte* utf8, int length)
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
/// Batch C1 runtime contract (ABI v2): one session per loaded module identity
/// (publisher + packageId + contentHash), activated exactly once; widget
/// instances are created per (contribution, instance) pair and destroyed by
/// opaque handle; the last successful destroy shuts the package down. NativeAOT
/// modules stay loaded for process lifetime by design, so a different content
/// hash for an already-loaded package is refused (restart required), matching
/// the ship strategy that package updates take effect after restart. UI thread
/// only; package code is never invoked under the manager lock.
/// </summary>
internal static class NativeWidgetRuntimeManager
{
    public const int RequiredAbiVersion = 2;
    public const string NativeRuntimeType = "native";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, NativePackageSession> Sessions = [];
    private static readonly Dictionary<string, string> LoadedModuleHashes = [];

    public static bool TryCreateInstance(
        NativePackageDescriptor descriptor,
        string contributionId,
        string instanceId,
        string dataDirectory,
        out NativeWidgetLease? lease)
    {
        lease = null;
        NativePackageSession? session = null;
        lock (Gate)
        {
            if (Sessions.TryGetValue(descriptor.Identity.Key, out NativePackageSession? existing))
            {
                if (!string.Equals(LoadedModuleHashes[descriptor.Identity.Key], descriptor.ContentHash, StringComparison.Ordinal))
                {
                    App.Log($"[NativePackage] {descriptor.Identity.Key} already loaded with a different content hash; restart required to activate the new version");
                    return false;
                }
                session = existing;
            }
        }
        if (session is null)
        {
            // Module open + package activate run OUTSIDE the manager lock.
            NativePackageSession? opened = NativeWidgetPackageLoader.TryOpenSession(descriptor, dataDirectory);
            if (opened is null) return false;
            lock (Gate)
            {
                if (Sessions.TryGetValue(descriptor.Identity.Key, out NativePackageSession? existing))
                {
                    if (!string.Equals(LoadedModuleHashes[descriptor.Identity.Key], descriptor.ContentHash, StringComparison.Ordinal))
                    {
                        App.Log($"[NativePackage] {descriptor.Identity.Key} was loaded concurrently with a different content hash; restart required");
                        return false;
                    }
                    session = existing;
                }
                else
                {
                    Sessions[descriptor.Identity.Key] = opened;
                    LoadedModuleHashes[descriptor.Identity.Key] = descriptor.ContentHash;
                    session = opened;
                }
            }
        }
        NativeWidgetLease? created = session.CreateInstance(contributionId, instanceId, dataDirectory);
        if (created is null) return false;
        lease = created;
        return true;
    }

    /// <summary>
    /// Product entry point (adapter seam until the package-format freeze adds
    /// runtime:native to the schema; until then every registry record is
    /// rejected here by construction - fail-closed).
    /// </summary>
    public static bool TryCreateFromInstalled(
        NativeInstalledPackageHandle handle,
        string contributionId,
        string instanceId,
        string dataDirectory,
        out NativeWidgetLease? lease)
    {
        lease = null;
        InstalledPackageRecord record = handle.Record;
        if (record.Runtime != NativeRuntimeType)
        {
            App.LogVerbose($"[NativePackage] installed package {record.PackageId} runtime '{record.Runtime}' is not native; refusing to activate");
            return false;
        }
        return TryCreateInstance(
            new NativePackageDescriptor(
                record.PublisherFingerprint,
                record.PackageId,
                record.ContentHash,
                handle.InstallRoot,
                NativeWidgetPackageLoader.ProductEntryModuleFileName),
            contributionId,
            instanceId,
            dataDirectory,
            out lease);
    }

    internal static void Release(NativeWidgetLease lease)
    {
        // Package destroy runs outside the manager lock; TryRelease only
        // commits the lease as released when the package reports success.
        if (!lease.TryRelease()) return;
        bool shutdown;
        lock (Gate)
        {
            shutdown = lease.Session.LiveInstanceCount == 0;
            if (shutdown)
            {
                Sessions.Remove(lease.Session.Identity.Key);
            }
        }
        if (shutdown)
        {
            lease.Session.Shutdown();
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

    /// <summary>
    /// Idempotent release; returns true only when this call performed a
    /// successful destroy. A failed destroy leaves the lease releasable again
    /// (retry) and keeps the instance counted, so shutdown cannot fire while
    /// the package still holds a live instance.
    /// </summary>
    internal bool TryRelease()
    {
        if (_released) return false;
        if (!_session.DestroyWidget(_handle)) return false;
        _released = true;
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
    private readonly object _instanceGate = new();
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
    internal int LiveInstanceCount { get { lock (_instanceGate) return _liveHandles.Count; } }

    internal static void Activate(NativePackageSession session)
    {
        var activate = (delegate* unmanaged[Cdecl]<char*, int, char*, int, NativeHostApiV1*, int>)session._activateExport;
        NativeHostApiV1 hostApi = NativeHostApiBridge.Create();
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

    internal NativeWidgetLease? CreateInstance(string contributionId, string instanceId, string dataDirectory)
    {
        string instanceDataRoot = Identity.ResolveInstanceDataRoot(dataDirectory, instanceId);
        var create = (delegate* unmanaged[Cdecl]<char*, int, char*, int, char*, int, nint*, nint*, int>)_createExport;
        nint handle = 0, viewAbi = 0;
        int status;
        fixed (char* contribution = contributionId)
        fixed (char* instance = instanceId)
        fixed (char* dataRoot = instanceDataRoot)
        {
            status = create(contribution, contributionId.Length, instance, instanceId.Length, dataRoot, instanceDataRoot.Length, &handle, &viewAbi);
        }
        if (status != 0)
        {
            // Partial outputs (handle/view set despite failure) must be rolled
            // back so the package never keeps an untracked instance.
            if (handle != 0) DestroyWidget(handle);
            if (viewAbi != 0) WinRT.MarshalInspectable<Microsoft.UI.Xaml.FrameworkElement>.DisposeAbi(viewAbi);
            App.Log($"[NativePackage] create {contributionId}/{instanceId} failed: 0x{status:X8}");
            return null;
        }
        if (handle == 0 || viewAbi == 0)
        {
            App.Log($"[NativePackage] create {contributionId}/{instanceId} returned incomplete outputs");
            return null;
        }
        Microsoft.UI.Xaml.FrameworkElement? view = null;
        try
        {
            view = WinRT.MarshalInspectable<Microsoft.UI.Xaml.FrameworkElement>.FromAbi(viewAbi);
        }
        catch (Exception error)
        {
            App.Log($"[NativePackage] view projection failed: {error.Message}");
        }
        finally
        {
            // Exactly one release of the ABI reference, success or failure.
            WinRT.MarshalInspectable<Microsoft.UI.Xaml.FrameworkElement>.DisposeAbi(viewAbi);
        }
        if (view is null)
        {
            // The package instance exists but the host cannot project it;
            // destroy the handle so no orphan stays in the live set.
            DestroyWidget(handle);
            return null;
        }
        view.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
        view.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
        lock (_instanceGate) _liveHandles.Add(handle);
        return NativeWidgetLease.Create(this, handle, view);
    }

    /// <summary>Destroys by handle; returns true only when the package confirms success.</summary>
    internal bool DestroyWidget(nint handle)
    {
        int status = ((delegate* unmanaged[Cdecl]<nint, int>)_destroyExport)(handle);
        if (status != 0)
        {
            App.Log($"[NativePackage] destroy 0x{handle:X} failed: 0x{status:X8}; instance remains counted");
            return false;
        }
        lock (_instanceGate) _liveHandles.Remove(handle);
        return true;
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
    public const string DevelopmentPackageDllFileName = "DeskBox.Glance.NativePackage.dll";
    public const string ProductEntryModuleFileName = "package.dll";
    public const string DevelopmentPublisherFingerprint = "dev-pilot";
    public const string DevelopmentContentHash = "dev";

    /// <summary>Path validation only - no module loading (unit-testable).</summary>
    public static string? TryGetDevelopmentPackageRoot()
    {
        string? configured = Environment.GetEnvironmentVariable(DevelopmentPackageEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured)) return null;
        try
        {
            string root = Path.GetFullPath(configured.Trim());
            if (!Directory.Exists(root)) return null;
            return File.Exists(Path.Combine(root, DevelopmentPackageDllFileName)) ? root : null;
        }
        catch
        {
            return null;
        }
    }

    public static NativePackageDescriptor CreateDevelopmentDescriptor(string packageRoot)
    {
        string packageId = new DirectoryInfo(packageRoot).Name;
        return new NativePackageDescriptor(
            DevelopmentPublisherFingerprint,
            packageId,
            DevelopmentContentHash,
            packageRoot,
            DevelopmentPackageDllFileName);
    }

    internal static unsafe NativePackageSession? TryOpenSession(NativePackageDescriptor descriptor, string dataDirectory)
    {
        try
        {
            string modulePath = Path.Combine(descriptor.PackageRoot, descriptor.EntryModuleFileName);
            string packageDataRoot = descriptor.Identity.ResolvePackageDataRoot(dataDirectory);
            Directory.CreateDirectory(packageDataRoot);
            nint module = NativeLibrary.Load(modulePath);
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
