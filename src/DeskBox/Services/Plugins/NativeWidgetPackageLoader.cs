using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    public nint SetInstanceConfigJson;
    // v4 (append-only): opaque per-session context. The package echoes it
    // back on config-changed registration and instance-config writes so the
    // host can attribute every call to the calling session (audit 20 §31).
    public nint Context;
}

/// <summary>
/// Per-session attribution the host attaches to every HostApi table. The
/// nint handed to the package is a registry id (never a real pointer), so
/// nothing is pinned and an unknown/forged id resolves to null.
/// </summary>
internal sealed class NativePackageContext
{
    internal required string PackageId { get; init; }
}

internal static class NativePackageContextRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<nint, NativePackageContext> Contexts = [];
    private static long _next;

    public static nint Register(NativePackageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (Gate)
        {
            nint id = (nint)Interlocked.Increment(ref _next);
            Contexts[id] = context;
            return id;
        }
    }

    public static void Unregister(nint context)
    {
        lock (Gate)
        {
            Contexts.Remove(context);
        }
    }

    public static NativePackageContext? TryResolve(nint context)
    {
        lock (Gate)
        {
            return Contexts.TryGetValue(context, out NativePackageContext? value) ? value : null;
        }
    }
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
    internal const uint CurrentVersion = 4;

    internal static NativeHostApiV1 Create(nint context) => new()
    {
        Size = (uint)sizeof(NativeHostApiV1),
        Version = CurrentVersion,
        Log = (nint)(delegate* unmanaged[Cdecl]<byte*, int, void>)&Log,
        GetConfigJson = (nint)(delegate* unmanaged[Cdecl]<byte*, int, int>)&GetConfigJson,
        SetConfigChangedHandler = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&SetConfigChangedHandler,
        SetInstanceConfigJson = (nint)(delegate* unmanaged[Cdecl]<char*, int, byte*, int, nint, int>)&SetInstanceConfigJson,
        Context = context,
    };

    // Per-session config-changed subscriptions (package -> host push).
    private static readonly object HandlerGate = new();
    private static readonly Dictionary<nint, nint> ConfigChangedHandlers = [];

    /// <summary>
    /// Fires every registered package config-changed callback. Callers are
    /// host-side setting sources (language/theme changes); a package
    /// exception is contained and logged - it must never reach the host.
    /// </summary>
    internal static void PushConfigChanged()
    {
        nint[] handlers;
        lock (HandlerGate)
        {
            handlers = [.. ConfigChangedHandlers.Values];
        }
        foreach (nint handler in handlers)
        {
            try
            {
                ((delegate* unmanaged[Cdecl]<void>)handler)();
            }
            catch (Exception error)
            {
                App.LogVerbose($"[NativePackage] config-changed callback failed: {error.Message}");
            }
        }
    }

    internal static void DetachSession(nint context)
    {
        NativePackageContextRegistry.Unregister(context);
        lock (HandlerGate)
        {
            ConfigChangedHandlers.Remove(context);
        }
    }

    /// <summary>
    /// Append-only JSON fields; the frozen HostApi v4 slots stay unchanged.
    /// The animation argument is the effective WindowsCompatibilityService.ShouldAnimate
    /// gate used by built-in Glance transitions, independent of rotation preferences.
    /// </summary>
    internal static string BuildConfigJson(string locale, string accent,
        string theme = "Dark", string materialType = "Mica",
        double materialOpacity = 0.8, double materialIntensity = 0.65,
        Models.AppSettings? performanceSettings = null, bool allowDecorativeAnimations = true)
    {
        // Use the same resolver as built-in Glance. The legacy aggregate
        // decorative-effects switch is only a compatibility mirror.
        bool allowImageAutoRotation = performanceSettings is null
            ? PerformanceSettingsPolicy.DefaultGlanceImageAutoRotationEnabled
            : PerformanceSettingsPolicy.Resolve(performanceSettings).AllowGlanceImageAutoRotation;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("locale", locale);
            writer.WriteString("accent", accent);
            writer.WriteString("theme", theme);
            writer.WriteString("materialType", materialType);
            writer.WriteNumber("materialOpacity", double.IsFinite(materialOpacity) ? Math.Clamp(materialOpacity, 0, 1) : 0.8);
            writer.WriteNumber("materialIntensity", double.IsFinite(materialIntensity) ? Math.Clamp(materialIntensity, 0, 1) : 0.65);
            writer.WriteBoolean("allowImageAutoRotation", allowImageAutoRotation);
            writer.WriteBoolean("allowDecorativeAnimations", allowDecorativeAnimations);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

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
            var themeService = App.Current?.ThemeService;
            var settings = App.Current?.SettingsService?.Settings;
            bool isDark = themeService?.CurrentTheme switch
            {
                Microsoft.UI.Xaml.ElementTheme.Light => false,
                Microsoft.UI.Xaml.ElementTheme.Dark => true,
                _ => Helpers.Win32Helper.IsSystemDarkMode(),
            };
            string accent = Helpers.AccentColorHelper.ToHex(
                themeService?.GetEffectiveAccentColor() ?? Helpers.AccentColorHelper.DefaultAccentColor);
            byte[] utf8 = Encoding.UTF8.GetBytes(BuildConfigJson(CurrentLocale(), accent,
                isDark ? "Dark" : "Light",
                WindowsCompatibilityService.ResolveWidgetMaterialType(settings?.WidgetMaterialType ?? SettingsService.WidgetMaterialTypeMica),
                settings?.WidgetOpacity ?? SettingsService.DefaultWidgetOpacity,
                settings?.WidgetMaterialIntensity ?? SettingsService.DefaultWidgetMaterialIntensity,
                settings, WindowsCompatibilityService.ShouldAnimate));
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
    private static int SetConfigChangedHandler(nint context, nint handler)
    {
        try
        {
            // Only sessions with a live context may subscribe (audit 20 §31:
            // attribution). handler=0 unsubscribes.
            if (NativePackageContextRegistry.TryResolve(context) is null)
            {
                return unchecked((int)0x80070057);
            }
            SubscribeConfigChanged(context, handler);
            App.LogVerbose($"[NativePackage] config-changed handler registered for context 0x{context:X}");
            return 0;
        }
        catch
        {
            return unchecked((int)0x80004005); // E_FAIL
        }
    }

    /// <summary>
    /// Managed seam for the subscription store (behavior-testable without a
    /// native call), also used by the callback above.
    /// </summary>
    internal static void SubscribeConfigChanged(nint context, nint handler)
    {
        lock (HandlerGate)
        {
            if (handler != 0)
            {
                ConfigChangedHandlers[context] = handler;
            }
            else
            {
                ConfigChangedHandlers.Remove(context);
            }
        }
    }

    internal static int RegisteredConfigChangedHandlerCount
    {
        get { lock (HandlerGate) return ConfigChangedHandlers.Count; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetInstanceConfigJson(char* instanceId, int instanceIdLength, byte* json, int jsonLength, nint context)
    {
        try
        {
            if (instanceId is null || json is null || jsonLength <= 0) return unchecked((int)0x80070057);
            string widgetId = new(instanceId, 0, instanceIdLength);
            string payload = Encoding.UTF8.GetString(json, jsonLength);
            // Generic routing with session attribution (audit 20 §31): the
            // echoed context identifies the calling session, and the
            // instance must belong to that package - a package cannot patch
            // another package's instance.
            NativePackageContext? owner = NativePackageContextRegistry.TryResolve(context);
            if (owner is null)
            {
                return unchecked((int)0x80070057);
            }
            string? registeredPackageId = PackageInstanceRegistry.TryResolvePackageId(widgetId);
            if (!string.Equals(registeredPackageId, owner.PackageId, StringComparison.Ordinal))
            {
                App.LogVerbose($"[NativePackage] config patch for instance {widgetId} does not belong to {owner.PackageId}; rejected");
                return unchecked((int)0x80070057); // E_INVALIDARG
            }
            OfficialPackageBinding? binding = PackageBindingRegistry.TryGetByPackageId(owner.PackageId);
            if (binding?.Migration is null || !binding.Migration.TryApplyPatch(widgetId, payload))
            {
                return unchecked((int)0x80070057); // E_INVALIDARG
            }
            // The adapter has ACCEPTED the patch; the authoritative store
            // update runs fire-and-forget (see the adapter's CommitAsync).
            // The return value means accepted, not committed.
            return 0;
        }
        catch (Exception error)
        {
            App.LogVerbose($"[NativePackage] instance config write-through failed: {error.Message}");
            return unchecked((int)0x80004005); // E_FAIL
        }
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
/// HostApi ABI FREEZE POLICY (audit round 21): HostApi v4 is the first
/// frozen baseline. Versions v1–v3 were internal pre-release experiments
/// and are not compatibility targets. From v4 onward, existing function
/// pointer slots AND their signatures are immutable — new capabilities are
/// added as NEW slots appended at the table end. The package's
/// Size/Version gate (RequiredHostApiVersion) ensures it never reads past
/// what the host provides.
/// </summary>

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
    // Serializes activate/create/destroy/shutdown for one package identity
    // without ever invoking package code under the global manager lock.
    private static readonly Dictionary<string, object> IdentityGates = [];
    // audit round 21 — Faulted/RestartRequired: a package whose shutdown
    // reported failure has unknown internal state, and the NativeAOT module
    // stays resident for the process lifetime. It must never be re-activated
    // in this process; the flag clears only on restart.
    private static readonly Dictionary<string, string> FaultedPackages = [];

    internal static bool IsFaulted(string identityKey)
    {
        lock (Gate)
        {
            return FaultedPackages.ContainsKey(identityKey);
        }
    }

    public static bool TryCreateInstance(
        NativePackageDescriptor descriptor,
        string contributionId,
        string instanceId,
        string dataDirectory,
        out NativeWidgetLease? lease)
    {
        lease = null;
        object identityGate = GetIdentityGate(descriptor.Identity.Key);
        lock (identityGate)
        {
            NativePackageSession? session;
            // audit round 21 — Faulted/RestartRequired: a package whose
            // shutdown failed has unknown resident state; refuse re-activation
            // until process restart even though the module is still loaded.
            lock (Gate)
            {
                if (FaultedPackages.TryGetValue(descriptor.Identity.Key, out string? reason))
                {
                    App.Log($"[NativePackage] {descriptor.Identity.Key} is faulted and cannot be re-activated in this process ({reason})");
                    return false;
                }
                // Module residency outlives sessions, including failed activation.
                if (!NativeWidgetPackageLoader.IsContentHashCompatible(descriptor))
                {
                    App.Log($"[NativePackage] {descriptor.Identity.Key} already loaded with a different content hash; restart required to activate the new version");
                    return false;
                }
                Sessions.TryGetValue(descriptor.Identity.Key, out session);
            }

            if (session is null)
            {
                // The identity gate guarantees exactly one open/activate path
                // for the first concurrent instance request.
                session = NativeWidgetPackageLoader.TryOpenSession(descriptor, dataDirectory);
                if (session is null) return false;
                lock (Gate) Sessions[descriptor.Identity.Key] = session;
            }

            NativeWidgetLease? created = session.CreateInstance(contributionId, instanceId, dataDirectory);
            if (created is null) return false;
            lease = created;
            return true;
        }
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
        object identityGate = GetIdentityGate(lease.Session.Identity.Key);
        lock (identityGate)
        {
            // Package destroy runs outside the manager lock; TryRelease only
            // commits the lease as released when the package reports success.
            if (!lease.TryRelease()) return;
            bool shutdown;
            lock (Gate)
            {
                shutdown = lease.Session.LiveInstanceCount == 0;
                if (shutdown &&
                    Sessions.TryGetValue(lease.Session.Identity.Key, out NativePackageSession? current) &&
                    ReferenceEquals(current, lease.Session))
                {
                    Sessions.Remove(lease.Session.Identity.Key);
                }
            }
            if (shutdown)
            {
                // audit round 21 — Faulted/RestartRequired: a failed package
                // shutdown leaves the resident module's state unknown, so the
                // identity is marked faulted before another create can enter.
                if (lease.Session.Shutdown())
                {
                    App.Log($"[NativePackage] {lease.Session.Identity.Key} dormant; re-activation allowed");
                }
                else
                {
                    string reason = "package shutdown reported failure; restart required";
                    lock (Gate) FaultedPackages[lease.Session.Identity.Key] = reason;
                    App.Log($"[NativePackage] {lease.Session.Identity.Key} marked FAULTED: {reason}");
                }
            }
        }
    }

    private static object GetIdentityGate(string identityKey)
    {
        lock (Gate)
        {
            if (!IdentityGates.TryGetValue(identityKey, out object? identityGate))
                IdentityGates[identityKey] = identityGate = new object();
            return identityGate;
        }
    }
}

/// <summary>One live widget instance; Dispose routes to the manager's release path.</summary>
internal sealed class NativeWidgetLease : IDisposable
{
    private NativePackageSession _session = null!;
    private nint _handle;
    private readonly object _operationGate = new();
    private bool _released;

    internal Microsoft.UI.Xaml.FrameworkElement View { get; private set; } = null!;
    private string _instanceId = null!;

    internal NativePackageSession Session => _session;

    internal static NativeWidgetLease Create(
        NativePackageSession session, nint handle, Microsoft.UI.Xaml.FrameworkElement view,
        string instanceId) => new()
    {
        _session = session,
        _handle = handle,
        View = view,
        _instanceId = instanceId,
    };

    /// <summary>
    /// Idempotent release; returns true only when this call performed a
    /// successful destroy. A failed destroy leaves the lease releasable again
    /// (retry) and keeps the instance counted, so shutdown cannot fire while
    /// the package still holds a live instance.
    /// </summary>
    internal bool TryRelease()
    {
        lock (_operationGate)
        {
            if (_released) return false;
            if (!_session.DestroyWidget(_handle))
            {
                App.LogVerbose($"[NativePackage] destroy retry pending for instance {_instanceId}");
                return false;
            }
            _released = true;
            return true;
        }
    }

    void IDisposable.Dispose() => NativeWidgetRuntimeManager.Release(this);

    internal Contracts.WidgetCompactBackgroundSnapshot? GetCompactBackground()
    {
        lock (_operationGate)
            return _released ? null : _session.GetCompactBackground(_handle);
    }

    /// <summary>Forward a host lifecycle event to the package (no-op if the package has no event export).</summary>
    internal void InvokeWidgetEvent(WidgetLifecycleEventKind kind, double width, double height, uint flags)
    {
        lock (_operationGate)
        {
            if (!_released) _session.SendWidgetEvent(_handle, kind, width, height, flags);
        }
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
    private readonly nint _compactBackgroundExport; // optional, ABI v4 unchanged
    private bool _active;
    private readonly HashSet<nint> _closingHandles = [];
    private readonly object _instanceGate = new();
    private readonly HashSet<nint> _liveHandles = [];
    // audit 20 §18: instance ids for PackageInstanceRegistry unregistration.
    // audit 21: instance data roots are NOT tracked (or touched) here —
    // runtime destroy keeps persistent data; logical widget deletion owns
    // data removal via NativeInstanceDataLifecycle.
    private readonly Dictionary<nint, string> _instanceIds = [];
    internal nint _hostApiContext;

    internal NativePackageSession(
        NativePackageIdentity identity,
        string packageRoot,
        string packageDataRoot,
        nint activateExport,
        nint createExport,
        nint destroyExport,
        nint shutdownExport,
        nint widgetEventExport,
        nint compactBackgroundExport = 0)
    {
        Identity = identity;
        PackageRoot = packageRoot;
        PackageDataRoot = packageDataRoot;
        _activateExport = activateExport;
        _createExport = createExport;
        _destroyExport = destroyExport;
        _shutdownExport = shutdownExport;
        _widgetEventExport = widgetEventExport;
        _compactBackgroundExport = compactBackgroundExport;
    }

    internal NativePackageIdentity Identity { get; }
    internal string PackageRoot { get; }
    internal string PackageDataRoot { get; }
    internal int LiveInstanceCount { get { lock (_instanceGate) return _liveHandles.Count; } }

    internal static void Activate(NativePackageSession session)
    {
        var activate = (delegate* unmanaged[Cdecl]<char*, int, char*, int, NativeHostApiV1*, int>)session._activateExport;
        // Per-session attribution (audit 20 §31): the package echoes this id
        // back on config-subscription and instance-config calls.
        session._hostApiContext = NativePackageContextRegistry.Register(
            new NativePackageContext { PackageId = session.Identity.PackageId });
        NativeHostApiV1 hostApi = NativeHostApiBridge.Create(session._hostApiContext);
        int status;
        fixed (char* package = session.PackageRoot)
        fixed (char* data = session.PackageDataRoot)
        {
            NativeHostApiV1* api = &hostApi;
            status = activate(package, session.PackageRoot.Length, data, session.PackageDataRoot.Length, api);
        }
        if (status != 0)
        {
            NativeHostApiBridge.DetachSession(session._hostApiContext);
            throw new InvalidOperationException($"[NativePackage] activate failed for {session.Identity.Key}: 0x{status:X8}");
        }
        session._active = true;
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
        lock (_instanceGate)
        {
            _liveHandles.Add(handle);
            _instanceIds[handle] = instanceId;
        }
        // Ownership for the generic write-through routing (audit 20 §18).
        PackageInstanceRegistry.Register(Identity.PackageId, instanceId);
        return NativeWidgetLease.Create(this, handle, view, instanceId);
    }

    /// <summary>
    /// Destroys by handle; returns true only when the package confirms success.
    /// Runtime destroy is lifecycle 1 of 3 (audit round 21): it releases the
    /// widget handle and ownership ONLY — the persistent instance data root
    /// is deliberately KEPT, because transient teardowns (group switches,
    /// content rebuilds, reparents) reuse the same instance. Persistent data
    /// removal belongs exclusively to logical widget deletion
    /// (NativeInstanceDataLifecycle.DeleteAsync) after the WidgetConfig
    /// deletion has committed.
    /// </summary>
    internal bool DestroyWidget(nint handle)
    {
        lock (_instanceGate)
        {
            if (!_closingHandles.Add(handle)) return false;
        }
        try
        {
            int status = ((delegate* unmanaged[Cdecl]<nint, int>)_destroyExport)(handle);
            if (status != 0)
            {
                App.Log($"[NativePackage] destroy 0x{handle:X} failed: 0x{status:X8}; instance remains counted");
                return false;
            }
            string? instanceId;
            lock (_instanceGate)
            {
                _liveHandles.Remove(handle);
                _instanceIds.Remove(handle, out instanceId);
            }
            if (instanceId is not null)
            {
                PackageInstanceRegistry.Unregister(instanceId);
            }
            return true;
        }
        finally
        {
            // Failed destroys retain the existing retry/counting contract.
            lock (_instanceGate) _closingHandles.Remove(handle);
        }
    }

    internal Contracts.WidgetCompactBackgroundSnapshot? GetCompactBackground(nint handle)
    {
        var snapshot = NativeWidgetCompactBackground.Read(
            _compactBackgroundExport, handle, () => CanReadCompactBackground(handle),
            static pointer => WinRT.MarshalInspectable<Microsoft.UI.Xaml.Media.ImageSource>.FromAbi(pointer),
            static pointer => WinRT.MarshalInspectable<Microsoft.UI.Xaml.Media.ImageSource>.DisposeAbi(pointer));
        return snapshot is { } value
            ? new Contracts.WidgetCompactBackgroundSnapshot(value.ImageSource, value.Opacity)
            : null;
    }

    private bool CanReadCompactBackground(nint handle)
    {
        lock (_instanceGate)
        {
            return _active && handle != 0 && _liveHandles.Contains(handle) && !_closingHandles.Contains(handle);
        }
    }

    /// <summary>Forward a lifecycle event through the versioned ABI v4 payload
    /// struct; logs when the package reports failure.</summary>
    internal unsafe void SendWidgetEvent(nint handle, WidgetLifecycleEventKind kind, double width, double height, uint flags)
    {
        if (_widgetEventExport == 0) return;
        lock (_instanceGate)
        {
            // A failed destroy keeps the lease usable for retry. Only block
            // delivery while the same handle is actively closing.
            if (_closingHandles.Contains(handle)) return;
        }
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

    /// <summary>
    /// Tears the session down. Returns true only when the package confirmed
    /// a clean shutdown — a false return means the resident module's
    /// internal state is unknown, and the caller must mark the package
    /// Faulted so it is never re-activated in this process (audit round 21).
    /// Session attribution (context + config subscription) is detached on
    /// EVERY path: the previous early-return leaked both on the normal
    /// shutdown path, accumulating stale callbacks across activate cycles.
    /// Persistent instance data roots are never touched here — runtime
    /// teardown is lifecycle 1; data removal belongs to logical deletion.
    /// </summary>
    internal bool Shutdown()
    {
        // Refuse reads before entering package code, including reentrant reads
        // during shutdown and reads after a failed shutdown.
        lock (_instanceGate) _active = false;
        bool succeeded;
        try
        {
            int status = ((delegate* unmanaged[Cdecl]<int>)_shutdownExport)();
            succeeded = status == 0;
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
            succeeded = false;
        }

        // Release ownership of any still-tracked instances (a destroy that
        // failed earlier, or a host that tore down without destroying).
        // Persistent data roots are KEPT — logical deletion owns them.
        lock (_instanceGate)
        {
            foreach (nint handle in _liveHandles.ToArray())
            {
                if (_instanceIds.Remove(handle, out string? instanceId))
                {
                    PackageInstanceRegistry.Unregister(instanceId);
                }
            }
            _liveHandles.Clear();
        }

        // No early return above this line: session attribution must detach
        // on every path (audit round 21 — the normal shutdown path used to
        // leak the context and config-changed subscription).
        NativeHostApiBridge.DetachSession(_hostApiContext);
        return succeeded;
    }
}

/// <summary>
/// Optional export payload, independent of mandatory ABI v4/HostApi. Layout
/// is fixed at 24 bytes on x64/ARM64. Size is caller capacity, Version is the
/// requested schema (1); both stay unchanged. Packages accept Size >= 24 and
/// Version == 1, touch only the v1 prefix, and leave any trailing bytes alone.
/// Future schemas must negotiate a new Version while continuing to serve v1.
/// Caller initializes ImageSource/Opacity to zero; opacity is finite [0,1].
/// Zero opacity or no image produces ImageSource=0, Opacity=0.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct NativeWidgetCompactBackgroundV1
{
    [FieldOffset(0)] public uint Size;
    [FieldOffset(4)] public uint Version;
    [FieldOffset(8)] public nint ImageSource;
    [FieldOffset(16)] public double Opacity;

    internal const uint CurrentVersion = 1;
}

/// <summary>
/// Optional C ABI snapshot read. The nonzero output is one owned IInspectable
/// reference, including partial failure outputs. Projection acquires its own
/// reference; the transferred reference is released exactly once. Delegates
/// keep ownership/error behavior testable without constructing WinUI objects.
/// Like the native widget runtime, callers must use the UI thread.
/// </summary>
internal static class NativeWidgetCompactBackground
{
    internal static unsafe (T ImageSource, double Opacity)? Read<T>(nint export, nint handle, Func<bool> canRead,
        Func<nint, T?> fromAbi, Action<nint> disposeAbi) where T : class
    {
        NativeWidgetCompactBackgroundV1 background = new()
        {
            Size = (uint)sizeof(NativeWidgetCompactBackgroundV1),
            Version = NativeWidgetCompactBackgroundV1.CurrentVersion,
        };
        try
        {
            try
            {
                if (export == 0 || handle == 0 || !canRead()) return null;
                int status = ((delegate* unmanaged[Cdecl]<nint, NativeWidgetCompactBackgroundV1*, int>)export)(handle, &background);
                if (status < 0 || background.Size != (uint)sizeof(NativeWidgetCompactBackgroundV1) ||
                    background.Version != NativeWidgetCompactBackgroundV1.CurrentVersion ||
                    background.ImageSource == 0 || !double.IsFinite(background.Opacity) ||
                    background.Opacity <= 0 || background.Opacity > 1 || !canRead()) return null;
                T? image = fromAbi(background.ImageSource);
                return image is not null && canRead() ? (image, background.Opacity) : null;
            }
            finally
            {
                if (background.ImageSource != 0) disposeAbi(background.ImageSource);
            }
        }
        catch
        {
            // Neither package/projection nor cleanup exceptions escape into
            // the host's compact-layout path. Never retry DisposeAbi.
            return null;
        }
    }
}

/// <summary>
/// Process-lifetime NativeAOT modules, independent of session activation/shutdown.
/// A successful native load permanently pins its identity/hash and handle, even
/// if export validation or activation later fails. There is no unload/reset path.
/// </summary>
internal sealed class NativePackageModuleRegistry
{
    private sealed class Entry(string contentHash)
    {
        internal string ContentHash { get; } = contentHash;
        internal nint Module { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<NativePackageIdentity, Entry> _modules = [];

    internal bool IsContentHashCompatible(NativePackageIdentity identity, string contentHash)
    {
        lock (_gate)
        {
            return !_modules.TryGetValue(identity, out Entry? entry) ||
                string.Equals(entry.ContentHash, contentHash, StringComparison.Ordinal);
        }
    }

    internal bool TryLoad(NativePackageIdentity identity, string contentHash,
        Func<nint> load, out nint module)
    {
        module = 0;
        Entry reservation;
        lock (_gate)
        {
            if (_modules.TryGetValue(identity, out Entry? existing))
            {
                // Also reject reentrant/concurrent loads while native loading
                // is in progress. Never run package code under this lock.
                if (!string.Equals(existing.ContentHash, contentHash, StringComparison.Ordinal) ||
                    existing.Module == 0) return false;
                module = existing.Module;
                return true;
            }
            reservation = new Entry(contentHash);
            _modules.Add(identity, reservation);
        }

        try
        {
            module = load();
            if (module == 0) return false;
            lock (_gate) reservation.Module = module;
            return true;
        }
        finally
        {
            // Only a load that never returned a valid handle can release the
            // reservation. ABI/activation work runs AFTER this method returns.
            if (module == 0)
            {
                lock (_gate) _modules.Remove(identity);
            }
        }
    }
}

/// <summary>Module loading + ABI resolution for the runtime manager (ABI v4).</summary>
internal static class NativeWidgetPackageLoader
{
    private static readonly NativePackageModuleRegistry Modules = new();

    internal static bool IsContentHashCompatible(NativePackageDescriptor descriptor) =>
        Modules.IsContentHashCompatible(descriptor.Identity, descriptor.ContentHash);

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
            // Pin the hash at the actual successful load, before ANY export
            // lookup, ABI check or activate call. Dormant sessions reuse this
            // handle without another LoadLibrary reference or a new DLL copy.
            if (!Modules.TryLoad(descriptor.Identity, descriptor.ContentHash,
                () => NativeLibrary.Load(modulePath), out nint module))
            {
                App.Log($"[NativePackage] {descriptor.Identity.Key} module load refused: different content hash (restart required) or load already in progress");
                return null;
            }
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
            // Optional capability: old ABI v4 packages remain valid. Resolve
            // only after the existing version handshake has succeeded.
            TryGetExport(module, "deskbox_widget_get_compact_background", out nint compactBackgroundExport);
            var session = new NativePackageSession(
                descriptor.Identity, descriptor.PackageRoot, packageDataRoot,
                activateExport, createExport, destroyExport, shutdownExport, widgetEventExport,
                compactBackgroundExport);
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
