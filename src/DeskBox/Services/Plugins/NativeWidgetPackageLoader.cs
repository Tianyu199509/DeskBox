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
/// Structurally paired installed-package handle: the registry record, the
/// verified package model, and the resolved install root travel together and
/// can only be produced through PluginPackageManager.TryCreateNativeHandle.
/// The verified model carries the actual EntryMain (not a runtime guess).
/// </summary>
public sealed record NativeInstalledPackageHandle
{
    internal InstalledPackageRecord Record { get; }
    internal VerifiedPluginPackage Verified { get; }
    internal string InstallRoot { get; }
    internal string EntryMain => Verified.EntryMain ?? "package.dll";

    internal NativeInstalledPackageHandle(InstalledPackageRecord record, VerifiedPluginPackage verified, string installRoot)
    {
        Record = record;
        Verified = verified;
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

/// <summary>
/// Versioned host→package lifecycle event payload (ABI v4). Must stay
/// layout-identical to the package-side DeskBoxWidgetEventV1 (pinned by
/// NativeWidgetLifecycleAbiTests). Append-only: future payload fields
/// consume Reserved slots or grow Size with a Version bump; existing
/// fields are never reordered or repurposed.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeWidgetEventV1
{
    public uint Size;
    public uint Version;
    public uint Kind;
    public uint Flags;
    public double Width;
    public double Height;
    public ulong Reserved0;
    public ulong Reserved1;
    public ulong Reserved2;
    public ulong Reserved3;

    public const uint CurrentVersion = 1;
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

    /// <summary>
    /// DeskBox's own language selection, not the OS UI culture - the user can
    /// override the OS locale in settings and the built-in widgets follow
    /// that choice (audit round 18). Falls back to the OS culture when the
    /// app instance is not available.
    /// </summary>
    private static string CurrentLocale() =>
        App.Current?.LocalizationService?.CurrentCultureName
        ?? System.Globalization.CultureInfo.CurrentUICulture.Name;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetConfigJson(byte* buffer, int bufferLength)
    {
        try
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(BuildConfigJson(CurrentLocale(), "#FF4CC2FF"));
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
/// Batch C1 runtime contract (ABI v4): one session per loaded module identity
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
    public const int RequiredAbiVersion = 4;
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
        // Contribution must exist in the verified manifest before the runtime
        // forwards it to the package DLL (audit round 13 §22).
        if (!handle.Verified.Contributions.Any(contribution =>
                string.Equals(contribution.Id, contributionId, StringComparison.Ordinal)))
        {
            App.LogVerbose($"[NativePackage] contribution '{contributionId}' not found in {record.PackageId}; refusing to create");
            return false;
        }
        return TryCreateInstance(
            new NativePackageDescriptor(
                record.PublisherFingerprint,
                record.PackageId,
                record.ContentHash,
                handle.InstallRoot,
                handle.EntryMain),
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

    /// <summary>Forward a host lifecycle event to the package (no-op if the package has no event export).</summary>
    internal void InvokeWidgetEvent(WidgetLifecycleEventKind kind, double width, double height, uint flags)
    {
        _session.SendWidgetEvent(_handle, kind, width, height, flags);
    }
}

/// <summary>Typed host→package lifecycle events (ABI v4, audit rounds 15-17). Wire
/// values are pinned against the package-side constants by NativeWidgetLifecycleAbiTests.</summary>
internal enum WidgetLifecycleEventKind : uint
{
    RefreshRequested = 1,
    AppearanceChanged = 2,
    Activated = 3,
    Deactivated = 4,
    VisibilityChanged = 5,     // flags bit 0: 1=visible, 0=hidden
    RevealCompleted = 6,
    LongHidden = 7,
    CompactStateChanged = 8,   // flags bit 0: 1=collapsed, 0=expanded
    ViewportChanged = 9,       // width/height carry the new size
    PerformanceSettingsChanged = 10,
    InteractiveResizeBegin = 11,
    InteractiveResizeEnd = 12,
    ResponsiveLayoutBegin = 13,   // capsule/breakpoint transition (not user drag)
    ResponsiveLayoutComplete = 14,
    ResponsiveLayoutCancel = 15,
}

internal sealed unsafe class NativePackageSession
{
    private readonly nint _activateExport;
    private readonly nint _createExport;
    private readonly nint _destroyExport;
    private readonly nint _shutdownExport;
    private readonly nint _widgetEventExport; // required export at ABI v4
    private readonly object _instanceGate = new();
    private readonly HashSet<nint> _liveHandles = [];

    internal NativePackageSession(
        NativePackageIdentity identity,
        string packageRoot,
        string packageDataRoot,
        nint activateExport,
        nint createExport,
        nint destroyExport,
        nint shutdownExport,
        nint widgetEventExport)
    {
        Identity = identity;
        PackageRoot = packageRoot;
        PackageDataRoot = packageDataRoot;
        _activateExport = activateExport;
        _createExport = createExport;
        _destroyExport = destroyExport;
        _shutdownExport = shutdownExport;
        _widgetEventExport = widgetEventExport;
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
        // Creation transaction (audit round 13): any incomplete output is
        // rolled back regardless of the reported status - a partial success
        // must never leave an untracked instance or a leaked ABI reference.
        if (status != 0 || handle == 0 || viewAbi == 0)
        {
            if (handle != 0) DestroyWidget(handle);
            if (viewAbi != 0) WinRT.MarshalInspectable<Microsoft.UI.Xaml.FrameworkElement>.DisposeAbi(viewAbi);
            if (status != 0)
            {
                App.Log($"[NativePackage] create {contributionId}/{instanceId} failed: 0x{status:X8}");
            }
            else
            {
                App.Log($"[NativePackage] create {contributionId}/{instanceId} returned incomplete outputs (handle={handle:X}, view={viewAbi:X}); rolled back");
            }
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

    /// <summary>Forward a lifecycle event through the versioned ABI v4 payload
    /// struct; logs when the package reports failure.</summary>
    internal unsafe void SendWidgetEvent(nint handle, WidgetLifecycleEventKind kind, double width, double height, uint flags)
    {
        if (_widgetEventExport == 0) return;
        try
        {
            NativeWidgetEventV1 payload = new()
            {
                Size = (uint)sizeof(NativeWidgetEventV1),
                Version = NativeWidgetEventV1.CurrentVersion,
                Kind = (uint)kind,
                Flags = flags,
                Width = width,
                Height = height,
            };
            var send = (delegate* unmanaged[Cdecl]<nint, NativeWidgetEventV1*, int>)_widgetEventExport;
            int status = send(handle, &payload);
            if (status != 0)
            {
                App.LogVerbose($"[NativePackage] widget event {kind} returned 0x{status:X8}");
            }
        }
        catch (Exception error)
        {
            App.LogVerbose($"[NativePackage] widget event {kind} failed: {error.Message}");
        }
    }

    internal void Shutdown()
    {
        try
        {
            int status = ((delegate* unmanaged[Cdecl]<int>)_shutdownExport)();
            if (status != 0)
            {
                App.Log($"[NativePackage] shutdown reported 0x{status:X8} for {Identity.Key}; the package still holds state (lifecycle bug upstream)");
            }
            else
            {
                App.Log($"[NativePackage] session shut down: {Identity.Key}");
            }
        }
        catch (Exception error)
        {
            App.Log($"[NativePackage] shutdown failed for {Identity.Key}: {error.Message}");
        }
    }
}

/// <summary>Module loading + ABI resolution for the runtime manager (ABI v4).</summary>
internal static class NativeWidgetPackageLoader
{
    public const string DevelopmentPackageEnvironmentVariable = "DESKBOX_DEV_NATIVE_GLANCE";
    public const string DevelopmentPackageDllFileName = "DeskBox.Glance.NativePackage.dll";
    public const string ProductEntryModuleFileName = "package.dll";

    /// <summary>
    /// Path validation only - no module loading (unit-testable). Compiled in
    /// all configurations on purpose: it stays inert unless a pilot-gated
    /// caller consumes it. Release builds have no such caller.
    /// </summary>
    public static string? TryGetDevelopmentPackageRoot()
    {
        string? configured = Environment.GetEnvironmentVariable(DevelopmentPackageEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured)) return null;
        try
        {
            string root = Path.GetFullPath(configured.Trim());
            if (!Directory.Exists(root)) return null;
            // Accept both the direct AOT output name and the official package
            // format name (package.dll after the build script renames it).
            bool hasDll = File.Exists(Path.Combine(root, DevelopmentPackageDllFileName)) ||
                          File.Exists(Path.Combine(root, ProductEntryModuleFileName));
            return hasDll ? root : null;
        }
        catch
        {
            return null;
        }
    }

#if DESKBOX_NATIVE_DEV_PILOT
    // Raw-directory load identity. Pilot builds only (audit round 17): the
    // descriptor builder and its constants compile out of Release so no
    // unverified module path can be constructed outside the B1 pipeline.
    public const string DevelopmentPublisherFingerprint = "dev-pilot";
    public const string DevelopmentContentHash = "dev";

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
#endif

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
                !TryGetExport(module, "deskbox_package_shutdown", out nint shutdownExport) ||
                !TryGetExport(module, "deskbox_widget_event", out nint widgetEventExport))
            {
                App.LogVerbose("[NativePackage] unified ABI v4 exports missing");
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
                activateExport, createExport, destroyExport, shutdownExport, widgetEventExport);
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
