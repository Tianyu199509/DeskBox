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
    }

    private static delegate* unmanaged[Cdecl]<byte*, int, void> _hostLog;

    /// <summary>HostApi table version this package build understands.</summary>
    private const uint RequiredHostApiVersion = 3;

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_activate", CallConvs = [typeof(CallConvCdecl)])]
    public static int Activate(char* packageRoot, int packageRootLength, char* packageDataRoot, int packageDataRootLength, HostApi* hostApi)
    {
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
            var controller = new Rendering.GlanceWidgetController(_packageRoot, contribution, instance, dataRoot);
            nint handle = ++_nextHandle;
            var lifecycleHandle = new Rendering.GlanceWidgetHandle(controller);
            _handles[handle] = lifecycleHandle;
            Instances[handle] = controller.View;
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

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static int DestroyWidget(nint widgetHandle)
    {
        // Unknown handles report E_HANDLE so host/package lifecycle drift stays
        // diagnosable instead of silently "succeeding" (audit round 17). The
        // host destroy state machine only commits a release on package success.
        try
        {
            if (!_handles.Remove(widgetHandle, out Rendering.GlanceWidgetHandle? handle)) return E_HANDLE;
            // Explicit teardown (audit 19): stop timers and save runtime
            // state now - never bet on the UI tree firing Unloaded.
            handle.Dispose();
            Instances.Remove(widgetHandle);
            return S_OK;
        }
        catch (Exception error)
        {
            TryWriteDiagnostic("destroy-error.txt", error.ToString());
            return error.HResult;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_shutdown", CallConvs = [typeof(CallConvCdecl)])]
    public static int Shutdown()
    {
        // Live instances at shutdown mean a lifecycle bug upstream (the host
        // only shuts down after the last successful destroy); surface it
        // instead of reporting success (audit round 17).
        if (Instances.Count > 0) return E_UNEXPECTED;
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
            DeskBox.GlancePackage.Services.HostConfig.Reset();
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
            if (!_handles.TryGetValue(widgetHandle, out Rendering.GlanceWidgetHandle? handle))
            {
                return E_HANDLE;
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
