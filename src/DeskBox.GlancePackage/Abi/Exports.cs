using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT;

namespace DeskBox.GlancePackage.Abi;

/// <summary>
/// Unified package ABI v4: deskbox_package_activate / deskbox_widget_create /
/// deskbox_widget_destroy / deskbox_package_shutdown / deskbox_widget_event.
/// The DLL is named package.dll per the official package format.
/// </summary>
public static unsafe class Exports
{
    private const int S_OK = 0;
    private const int E_HANDLE = unchecked((int)0x80070006);     // HRESULT_FROM_WIN32(ERROR_INVALID_HANDLE = 6)
    private const int E_INVALIDARG = unchecked((int)0x80070057); // HRESULT_FROM_WIN32(ERROR_INVALID_PARAMETER = 87)
    private const int E_POINTER = unchecked((int)0x80004003);
    private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

    private static string _packageRoot = "";
    private static string _packageDataRoot = "";
    private static Services.GlanceImageRepository? _imageRepository;
    private static readonly object InstanceGate = new();
    private static bool _compactBackgroundReady;
    private static bool _active;
    private static int _activationGeneration;
    private static readonly HashSet<nint> ClosingHandles = [];
    private static readonly Dictionary<nint, object> Instances = [];
    private static nint _nextHandle = 0x1000;

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_get_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetAbiVersion() => 4;

    /// <summary>
    /// Versioned host→package lifecycle event payload (ABI v4). Must stay
    /// layout-identical to the host-side NativeWidgetEventV1 (pinned by
    /// NativeWidgetLifecycleAbiTests). Append-only: future payload fields
    /// consume Reserved slots or grow Size with a Version bump; existing
    /// fields are never reordered or repurposed.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DeskBoxWidgetEventV1
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

    [StructLayout(LayoutKind.Sequential)]
    public struct HostApi
    {
        public uint Size;
        public uint Version;
        public nint Log;
        public nint GetConfigJson;
        public nint SetConfigChangedHandler;
        public nint SetInstanceConfigJson;
        // v4 (append-only): opaque per-session context - echo it back on
        // SetConfigChangedHandler and SetInstanceConfigJson calls.
        public nint Context;
    }

    private static delegate* unmanaged[Cdecl]<byte*, int, void> _hostLog;
    private static nint _hostContext;
    private static Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    /// <summary>HostApi table version this package build understands.</summary>
    private const uint RequiredHostApiVersion = 4;

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_activate", CallConvs = [typeof(CallConvCdecl)])]
    public static int Activate(char* packageRoot, int packageRootLength, char* packageDataRoot, int packageDataRootLength, HostApi* hostApi)
    {
        lock (InstanceGate)
        {
            _active = false;
            _compactBackgroundReady = false;
            _activationGeneration++;
        }
        try
        {
            _packageRoot = new string(packageRoot, 0, packageRootLength);
            _packageDataRoot = new string(packageDataRoot, 0, packageDataRootLength);
            Directory.CreateDirectory(_packageDataRoot);
            // Route package-side verbose logging to the host callback (D3
            // Phase 2: replaces the silent App.LogVerbose seam).
            DeskBox.GlancePackage.Services.PackageLogger.Sink = static message => HostLog(message);
            if (hostApi is not null)
            {
                // The HostApi table is a versioned contract: never read
                // function pointers before Version/Size prove they are there
                // (audit round 18 - this is a real product path now).
                if (hostApi->Version < RequiredHostApiVersion || hostApi->Size < (uint)sizeof(HostApi))
                {
                    TryWriteDiagnostic("activate-hostapi.txt",
                        $"hostApi version={hostApi->Version} size={hostApi->Size} requiredVersion={RequiredHostApiVersion}");
                    return E_INVALIDARG;
                }
                if (hostApi->Log != 0)
                {
                    _hostLog = (delegate* unmanaged[Cdecl]<byte*, int, void>)hostApi->Log;
                    HostLog("glance package activated (abi 4)");
                }
                if (hostApi->GetConfigJson != 0)
                {
                    DeskBox.GlancePackage.Services.HostConfig.Initialize(hostApi->GetConfigJson);
                }
                if (hostApi->SetInstanceConfigJson != 0)
                {
                    DeskBox.GlancePackage.Services.HostConfig.InitializeSetInstanceConfig(hostApi->SetInstanceConfigJson);
                }
                // Session attribution + live config subscription (v4).
                _hostContext = hostApi->Context;
                DeskBox.GlancePackage.Services.HostConfig.InitializeContext(_hostContext);
                _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                if (hostApi->SetConfigChangedHandler != 0 && _dispatcher is not null)
                {
                    var subscribe = (delegate* unmanaged[Cdecl]<nint, nint, int>)hostApi->SetConfigChangedHandler;
                    _ = subscribe(_hostContext, (nint)(delegate* unmanaged[Cdecl]<void>)&OnConfigChanged);
                }
            }
            _imageRepository = new Services.GlanceImageRepository(_packageDataRoot);
            lock (InstanceGate)
            {
                _active = true;
                // Only advertise image reads for a successfully negotiated host.
                _compactBackgroundReady = hostApi is not null;
            }
            return S_OK;
        }
        catch (Exception error)
        {
            TryWriteDiagnostic("activate-error.txt", error.ToString());
            return error.HResult;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_create", CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateWidget(char* contributionId, int contributionIdLength, char* instanceId, int instanceIdLength, char* instanceDataRoot, int instanceDataRootLength, nint* widgetHandle, nint* view)
    {
        if (widgetHandle is null || view is null) return -1;
        *widgetHandle = 0;
        *view = 0;
        try
        {
            string contribution = new(contributionId, 0, contributionIdLength);
            string instance = new(instanceId, 0, instanceIdLength);
            string dataRoot = new(instanceDataRoot, 0, instanceDataRootLength);
            Directory.CreateDirectory(dataRoot);
            var controller = new Rendering.GlanceWidgetController(_packageRoot, contribution, instance, dataRoot,
                _imageRepository ?? throw new InvalidOperationException("Package image repository is not active."));
            var lifecycleHandle = new Rendering.GlanceWidgetHandle(controller);
            nint handle;
            lock (InstanceGate)
            {
                handle = ++_nextHandle;
                _handles[handle] = lifecycleHandle;
                Instances[handle] = controller.View;
            }
            *widgetHandle = handle;
            *view = WinRT.MarshalInspectable<FrameworkElement>.FromManaged(controller.View);
            HostLog($"widget created: {contribution}/{instance}");
            return S_OK;
        }
        catch (Exception error)
        {
            TryWriteDiagnostic("create-error.txt", error.ToString());
            return error.HResult;
        }
    }

    /// <summary>
    /// Optional snapshot schema v1 (mandatory package ABI stays v4).
    /// Caller supplies Size >= 24, Version = 1 and zero outputs. Size/Version
    /// remain unchanged; only the 24-byte v1 prefix may be written. Unknown
    /// versions fail; future implementations must continue supporting v1.
    /// A nonzero ImageSource transfers exactly one owned WinRT ImageSource
    /// IInspectable reference, even on failure. Caller always releases it.
    /// Opacity is finite [0,1]; no image/zero opacity returns both outputs zero.
    /// No decoding or XAML-tree inspection occurs.
    /// Called on the same UI thread as create/event/destroy.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_get_compact_background", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetCompactBackground(nint widgetHandle, DeskBoxCompactBackgroundV1* background)
    {
        if (background is null) return E_POINTER;
        try
        {
            int validation = InitializeCompactBackground(ref *background);
            if (validation < 0) return validation;
            Rendering.GlanceWidgetHandle handle;
            lock (InstanceGate)
            {
                if (!_active || !_compactBackgroundReady) return E_UNEXPECTED;
                if (widgetHandle == 0 || ClosingHandles.Contains(widgetHandle) ||
                    !_handles.TryGetValue(widgetHandle, out handle!)) return E_HANDLE;
            }
            double opacity = handle.Controller.CompactBackgroundOpacity;
            if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1) return E_INVALIDARG;
            if (opacity == 0) return S_OK;
            Microsoft.UI.Xaml.Media.ImageSource? image = handle.Controller.CompactBackgroundImage;
            if (image is not null)
            {
                background->ImageSource = WinRT.MarshalInspectable<Microsoft.UI.Xaml.Media.ImageSource>.FromManaged(image);
                if (background->ImageSource != 0) background->Opacity = opacity;
            }
            return S_OK;
        }
        catch (Exception error)
        {
            TryWriteDiagnostic("compact-background-error.txt", error.ToString());
            return error.HResult < 0 ? error.HResult : unchecked((int)0x80004005);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct DeskBoxCompactBackgroundV1
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Version;
        [FieldOffset(8)] public nint ImageSource;
        [FieldOffset(16)] public double Opacity;

        public const uint CurrentVersion = 1;
    }

    // Shared by the export and headless validation tests. A short buffer is
    // rejected before touching Version or outputs. Full buffers have outputs
    // cleared even on a version error. Incoming pointers must be zero/empty;
    // initialization never releases caller-owned values.
    internal static int InitializeCompactBackground(ref DeskBoxCompactBackgroundV1 background)
    {
        if (background.Size < (uint)sizeof(DeskBoxCompactBackgroundV1)) return E_INVALIDARG;
        background.ImageSource = 0;
        background.Opacity = 0;
        return background.Version == DeskBoxCompactBackgroundV1.CurrentVersion ? S_OK : E_INVALIDARG;
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static int DestroyWidget(nint widgetHandle)
    {
        // Unknown handles report E_HANDLE so host/package lifecycle drift stays
        // diagnosable instead of silently "succeeding" (audit round 17). The
        // host destroy state machine only commits a release on package success.
        bool markedClosing = false;
        try
        {
            Rendering.GlanceWidgetHandle handle;
            lock (InstanceGate)
            {
                if (!_handles.TryGetValue(widgetHandle, out handle!) ||
                    !ClosingHandles.Add(widgetHandle)) return E_HANDLE;
                markedClosing = true;
            }
            // Dispose FIRST, then remove the handle (audit round 20): the
            // controller's Dispose is total/no-throw, so nothing between the
            // two steps can fail and leave the host lease alive against an
            // already-gone package handle (the destroy transaction contract).
            handle.Dispose();
            lock (InstanceGate)
            {
                _handles.Remove(widgetHandle);
                Instances.Remove(widgetHandle);
            }
            return S_OK;
        }
        catch (Exception error)
        {
            TryWriteDiagnostic("destroy-error.txt", error.ToString());
            return error.HResult;
        }
        finally
        {
            if (markedClosing)
            {
                lock (InstanceGate) ClosingHandles.Remove(widgetHandle);
            }
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_shutdown", CallConvs = [typeof(CallConvCdecl)])]
    public static int Shutdown()
    {
        // Live instances at shutdown mean a lifecycle bug upstream (the host
        // only shuts down after the last successful destroy); surface it
        // instead of reporting success (audit round 17).
        lock (InstanceGate)
        {
            if (Instances.Count > 0) return E_UNEXPECTED;
            _active = false;
            _compactBackgroundReady = false;
            _activationGeneration++;
        }
        try
        {
            File.WriteAllText(Path.Combine(_packageDataRoot, "glance-session.txt"),
                $"instances={Instances.Count} shutdown={DateTime.Now:O}");
            HostLog("glance package shutdown");
            return S_OK;
        }
        catch (Exception error)
        {
            TryWriteDiagnostic("shutdown-error.txt", error.ToString());
            return error.HResult;
        }
        finally
        {
            // The module stays resident for process lifetime, but the next
            // activate must not observe stale host callbacks (audit 18).
            _hostLog = null;
            _hostContext = 0;
            _dispatcher = null;
            DeskBox.GlancePackage.Services.HostConfig.Reset();
            _imageRepository?.Dispose();
            _imageRepository = null;
            DeskBox.GlancePackage.Services.PackageLogger.Sink = null;
        }
    }

    /// <summary>Widget lifecycle event kinds (ABI v4). Wire contract — keep in
    /// sync with the host-side WidgetLifecycleEventKind enum (pinned by
    /// NativeWidgetLifecycleAbiTests).</summary>
    public const uint RefreshRequested = 1;
    public const uint AppearanceChanged = 2;
    public const uint Activated = 3;
    public const uint Deactivated = 4;
    public const uint VisibilityChanged = 5;     // Flags bit 0: 1=visible, 0=hidden
    public const uint RevealCompleted = 6;
    public const uint LongHidden = 7;
    public const uint CompactStateChanged = 8;   // Flags bit 0: 1=collapsed, 0=expanded
    public const uint ViewportChanged = 9;       // Width/Height carry the new size
    public const uint PerformanceSettingsChanged = 10;
    public const uint InteractiveResizeBegin = 11;
    public const uint InteractiveResizeEnd = 12;
    public const uint ResponsiveLayoutBegin = 13;   // capsule/breakpoint transition (not user drag)
    public const uint ResponsiveLayoutComplete = 14;
    public const uint ResponsiveLayoutCancel = 15;

    private static readonly Dictionary<nint, Rendering.GlanceWidgetHandle> _handles = [];

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_event", CallConvs = [typeof(CallConvCdecl)])]
    public static int WidgetEvent(nint widgetHandle, DeskBoxWidgetEventV1* payload)
    {
        // Total function: managed exceptions must never cross the C ABI boundary.
        try
        {
            if (payload is null) return E_POINTER;
            if (payload->Version != DeskBoxWidgetEventV1.CurrentVersion ||
                payload->Size < (uint)sizeof(DeskBoxWidgetEventV1))
            {
                return E_INVALIDARG;
            }
            Rendering.GlanceWidgetHandle handle;
            lock (InstanceGate)
            {
                if (ClosingHandles.Contains(widgetHandle) ||
                    !_handles.TryGetValue(widgetHandle, out handle!)) return E_HANDLE;
            }
            // Range is derived from the table itself, never a magic number, so
            // adding a kind cannot silently strand it outside the accepted range.
            if (payload->Kind is < RefreshRequested or > ResponsiveLayoutCancel)
            {
                return E_INVALIDARG;
            }
            handle.OnLifecycleEvent(payload->Kind, payload->Width, payload->Height, payload->Flags);
            return S_OK;
        }
        catch (Exception error)
        {
            TryWriteDiagnostic("widget-event-error.txt", error.ToString());
            return error.HResult;
        }
    }

    /// <summary>
    /// Host pushes this on language and appearance changes. Runs on the
    /// host UI thread - identical to the widgets' dispatcher - but the work
    /// is enqueued anyway so the callback stays trivial and re-entrant safe.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnConfigChanged()
    {
        try
        {
            Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = _dispatcher;
            int generation;
            lock (InstanceGate)
            {
                if (!_active || dispatcher is null) return;
                generation = _activationGeneration;
            }
            dispatcher.TryEnqueue(() => RefreshAllForConfigChange(generation));
        }
        catch
        {
            // Never let an exception cross back into the host.
        }
    }

    private static void RefreshAllForConfigChange(int generation)
    {
        try
        {
            Rendering.GlanceWidgetHandle[] handles;
            lock (InstanceGate)
            {
                if (!_active || generation != _activationGeneration) return;
                handles = [.. _handles.Values];
            }
            CultureInfo culture = DeskBox.GlancePackage.Services.HostConfig.TryGetCulture()
                ?? CultureInfo.CurrentUICulture;
            DeskBox.GlancePackage.Services.PackageStrings.Configure(culture, _packageRoot);
            foreach (Rendering.GlanceWidgetHandle handle in handles)
            {
                handle.Controller.ApplyConfigChange(culture);
            }
        }
        catch (Exception error)
        {
            TryWriteDiagnostic("config-change-error.txt", error.ToString());
        }
    }

    private static void HostLog(string message)
    {
        if (_hostLog is null) return;
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(message);
        fixed (byte* pointer = utf8) _hostLog(pointer, utf8.Length);
    }

    private static void TryWriteDiagnostic(string fileName, string content)
    {
        try
        {
            string root = string.IsNullOrEmpty(_packageDataRoot) ? Path.GetTempPath() : _packageDataRoot;
            File.WriteAllText(Path.Combine(root, fileName), content);
        }
        catch { }
    }
}
