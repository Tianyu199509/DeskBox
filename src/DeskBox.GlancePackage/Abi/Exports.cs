using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT;

namespace DeskBox.GlancePackage.Abi;

/// <summary>
/// Unified package ABI v2: deskbox_package_activate / deskbox_widget_create /
/// deskbox_widget_destroy / deskbox_package_shutdown. The DLL is named
/// package.dll per the official package format.
/// </summary>
public static unsafe class Exports
{
    private static string _packageRoot = "";
    private static string _packageDataRoot = "";
    private static readonly Dictionary<nint, object> Instances = [];
    private static nint _nextHandle = 0x1000;

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_get_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetAbiVersion() => 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct HostApi
    {
        public uint Size;
        public uint Version;
        public nint Log;
        public nint GetConfigJson;
        public nint SetConfigChangedHandler;
    }

    private static delegate* unmanaged[Cdecl]<byte*, int, void> _hostLog;

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_activate", CallConvs = [typeof(CallConvCdecl)])]
    public static int Activate(char* packageRoot, int packageRootLength, char* packageDataRoot, int packageDataRootLength, HostApi* hostApi)
    {
        try
        {
            _packageRoot = new string(packageRoot, 0, packageRootLength);
            _packageDataRoot = new string(packageDataRoot, 0, packageDataRootLength);
            Directory.CreateDirectory(_packageDataRoot);
            if (hostApi is not null && hostApi->Log != 0)
            {
                _hostLog = (delegate* unmanaged[Cdecl]<byte*, int, void>)hostApi->Log;
                HostLog("glance package activated (abi 2)");
            }
            return 0;
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
            FrameworkElement content = Rendering.GlanceViewBuilder.Create(_packageRoot, contribution, instance, dataRoot);
            nint handle = ++_nextHandle;
            var lifecycleHandle = new Rendering.GlanceWidgetHandle(content);
            _handles[handle] = lifecycleHandle;
            Instances[handle] = content;
            *widgetHandle = handle;
            *view = WinRT.MarshalInspectable<FrameworkElement>.FromManaged(content);
            HostLog($"widget created: {contribution}/{instance}");
            return 0;
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
        _handles.Remove(widgetHandle);
        Instances.Remove(widgetHandle);
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_shutdown", CallConvs = [typeof(CallConvCdecl)])]
    public static int Shutdown()
    {
        try
        {
            File.WriteAllText(Path.Combine(_packageDataRoot, "glance-session.txt"),
                $"instances={Instances.Count} shutdown={DateTime.Now:O}");
            HostLog("glance package shutdown");
            return 0;
        }
        catch { return 0; }
    }

    /// <summary>Widget lifecycle event kinds (ABI v3).</summary>
    public const uint RefreshRequested = 1;
    public const uint AppearanceChanged = 2;
    public const uint Activated = 3;
    public const uint Deactivated = 4;
    public const uint VisibilityChanged = 5;
    public const uint RevealCompleted = 6;
    public const uint LongHidden = 7;
    public const uint CompactStateChanged = 8;
    public const uint ViewportChanged = 9;
    public const uint PerformanceSettingsChanged = 10;
    public const uint InteractiveResizeBegin = 11;
    public const uint InteractiveResizeEnd = 12;

    private static readonly Dictionary<nint, Rendering.GlanceWidgetHandle> _handles = [];

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_event", CallConvs = [typeof(CallConvCdecl)])]
    public static int WidgetEvent(nint widgetHandle, uint eventKind, double width, double height, uint flags)
    {
        // Total function: managed exceptions must never cross the C ABI boundary.
        try
        {
            if (!_handles.TryGetValue(widgetHandle, out Rendering.GlanceWidgetHandle? handle))
            {
                return unchecked((int)0x80070510); // ERROR_INVALID_HANDLE
            }
            if (eventKind is < 1 or > 12)
            {
                return unchecked((int)0x80070057); // E_INVALIDARG
            }
            handle.OnLifecycleEvent(eventKind, width, height, flags);
            return 0;
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
