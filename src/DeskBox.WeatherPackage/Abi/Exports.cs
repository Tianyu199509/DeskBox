using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeskBox.WeatherPackage.Rendering;
using DeskBox.WeatherPackage.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeskBox.WeatherPackage.Abi;

/// <summary>Official Weather ABI v4. Structs and event ids match the frozen host contract.</summary>
public static unsafe class Exports
{
    private const int E_HANDLE = unchecked((int)0x80070006);
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_POINTER = unchecked((int)0x80004003);
    private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    private static WeatherPackageSession? _session;
    private static DispatcherQueue? _dispatcher;
    private static readonly Dictionary<nint, WeatherWidgetController> Instances = [];
    private static nint _nextHandle = 0x1000;

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


    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_get_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetAbiVersion() => 4;

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_activate", CallConvs = [typeof(CallConvCdecl)])]
    public static int Activate(char* packageRoot, int packageRootLength, char* dataRoot, int dataRootLength, HostApi* hostApi)
    {
        try
        {
            if (_session is not null || Instances.Count > 0) return E_UNEXPECTED;
            if (!ValidText(packageRoot, packageRootLength) || !ValidText(dataRoot, dataRootLength) ||
                hostApi is null || hostApi->Version < 4 || hostApi->Size < sizeof(HostApi)) return E_INVALIDARG;
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            if (_dispatcher is null || !_dispatcher.HasThreadAccess) return E_UNEXPECTED;
            // Each NativeAOT DLL owns its CLR state. The host runtime's UI
            // SynchronizationContext is not visible here; async continuations
            // must capture this package runtime's dispatcher explicitly.
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(_dispatcher));
            string root = new(packageRoot, 0, packageRootLength);
            string data = new(dataRoot, 0, dataRootLength);
            if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(data)) return E_INVALIDARG;
            var host = new WeatherHostConnection(hostApi->Context, hostApi->Log, hostApi->GetConfigJson,
                hostApi->SetConfigChangedHandler, hostApi->SetInstanceConfigJson);
            PackageLogger.Sink = host.WriteLog;
            _session = new WeatherPackageSession(root, data, host);
            host.Subscribe((nint)(delegate* unmanaged[Cdecl]<void>)&OnConfigChanged);
            PackageLogger.Log("[WeatherPackage] activated ABI=4");
            return 0;
        }
        catch (Exception error)
        {
            PackageLogger.Log("[WeatherPackage] activation failed: " + error);
            try { _session?.Dispose(); } catch { }
            _session = null;
            PackageLogger.Sink = null;
            return error.HResult;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_create", CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateWidget(char* contributionId, int contributionIdLength, char* instanceId, int instanceIdLength,
        char* instanceDataRoot, int instanceDataRootLength, nint* widgetHandle, nint* view)
    {
        if (widgetHandle is null || view is null) return E_POINTER;
        *widgetHandle = 0;
        *view = 0;
        WeatherWidgetController? controller = null;
        nint abi = 0;
        try
        {
            if (_session is null || _dispatcher?.HasThreadAccess != true) return E_UNEXPECTED;
            if (!ValidText(contributionId, contributionIdLength) || !ValidText(instanceId, instanceIdLength) ||
                !ValidText(instanceDataRoot, instanceDataRootLength)) return E_INVALIDARG;
            if (new string(contributionId, 0, contributionIdLength) != "weather") return E_INVALIDARG;
            string id = new(instanceId, 0, instanceIdLength);
            string data = new(instanceDataRoot, 0, instanceDataRootLength);
            if (!Path.IsPathFullyQualified(data)) return E_INVALIDARG;
            controller = new WeatherWidgetController(_session, id, data);
            abi = WinRT.MarshalInspectable<FrameworkElement>.FromManaged(controller.View);
            nint handle = ++_nextHandle;
            Instances.Add(handle, controller);
            *widgetHandle = handle;
            *view = abi;
            PackageLogger.Log($"[WeatherPackage] created instance={id} live={Instances.Count}");
            return 0;
        }
        catch (Exception error)
        {
            if (abi != 0) WinRT.MarshalInspectable<FrameworkElement>.DisposeAbi(abi);
            try { controller?.Dispose(); } catch { }
            PackageLogger.Log("[WeatherPackage] create failed: " + error);
            return error.HResult;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static int DestroyWidget(nint handle)
    {
        try
        {
            if (_dispatcher?.HasThreadAccess != true) return E_UNEXPECTED;
            if (!Instances.TryGetValue(handle, out var controller)) return E_HANDLE;
            controller.Dispose();
            Instances.Remove(handle);
            PackageLogger.Log($"[WeatherPackage] destroyed handle={handle} live={Instances.Count}");
            return 0;
        }
        catch (Exception error) { PackageLogger.Log(error.ToString()); return error.HResult; }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_shutdown", CallConvs = [typeof(CallConvCdecl)])]
    public static int Shutdown()
    {
        if (Instances.Count > 0) return E_UNEXPECTED;
        try
        {
            _session?.Dispose();
            PackageLogger.Log("[WeatherPackage] shutdown live=0");
            return 0;
        }
        catch (Exception error) { PackageLogger.Log(error.ToString()); return error.HResult; }
        finally { _session = null; _dispatcher = null; PackageLogger.Sink = null; }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_event", CallConvs = [typeof(CallConvCdecl)])]
    public static int WidgetEvent(nint handle, DeskBoxWidgetEventV1* payload)
    {
        try
        {
            if (payload is null) return E_POINTER;
            if (_dispatcher?.HasThreadAccess != true) return E_UNEXPECTED;
            if (payload->Version != 1 || payload->Size < sizeof(DeskBoxWidgetEventV1) ||
                payload->Kind < RefreshRequested || payload->Kind > ResponsiveLayoutCancel) return E_INVALIDARG;
            if (!Instances.TryGetValue(handle, out var controller)) return E_HANDLE;
            if ((payload->Kind is 9 or 11 or 12 or 13 or 14) &&
                (!double.IsFinite(payload->Width) || !double.IsFinite(payload->Height) ||
                 payload->Width < 0 || payload->Height < 0)) return E_INVALIDARG;
            controller.OnEvent(payload->Kind, payload->Width, payload->Height, payload->Flags);
            return 0;
        }
        catch (Exception error) { PackageLogger.Log(error.ToString()); return error.HResult; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnConfigChanged()
    {
        try
        {
            WeatherPackageSession? captured = _session;
            _dispatcher?.TryEnqueue(() =>
            {
                if (captured is null || !ReferenceEquals(captured, _session)) return;
                try
                {
                    string locale = captured.Host.ReadLocale();
                    if (!string.Equals(locale, captured.Localization.CurrentCultureName, StringComparison.OrdinalIgnoreCase))
                    {
                        captured.Localization.SetCulture(locale);
                    }
                    foreach (var controller in Instances.Values.ToArray()) controller.RefreshConfig();
                }
                catch (Exception error) { PackageLogger.Log("[WeatherPackage] config refresh: " + error); }
            });
        }
        catch { }
    }

    private static bool ValidText(char* text, int length) => text is not null && length is > 0 and <= 32768;
}
