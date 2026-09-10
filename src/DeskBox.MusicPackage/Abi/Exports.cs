using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using DeskBox.MusicPackage.Rendering;
using DeskBox.MusicPackage.Services;

namespace DeskBox.MusicPackage.Abi;

// Wire layout is deliberately identical to the frozen host ABI v4.
public static unsafe class Exports
{
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int InvalidHandle = unchecked((int)0x80070006);
    private const int Unexpected = unchecked((int)0x8000FFFF);
    private static readonly Dictionary<nint, MusicWidgetController> Instances = [];
    private static long _nextHandle;
    private static bool _active;
    private static int _generation;
    private static string _packageRoot = "";
    private static HostApi _host;
    private static DispatcherQueue? _dispatcher;

    [StructLayout(LayoutKind.Sequential)]
    public struct HostApi
    {
        public uint Size, Version;
        public nint Log, GetConfigJson, SetConfigChangedHandler, SetInstanceConfigJson, Context;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct DeskBoxWidgetEventV1
    {
        public uint Size, Version, Kind, Flags;
        public double Width, Height;
        public ulong Reserved0, Reserved1, Reserved2, Reserved3;
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_get_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetAbiVersion() => 4;

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_activate", CallConvs = [typeof(CallConvCdecl)])]
    public static int Activate(char* root, int rootLength, char* dataRoot, int dataLength, HostApi* host)
    {
        try
        {
            if (_active || !ValidText(root, rootLength) || !ValidText(dataRoot, dataLength) ||
                host is null || host->Version < 4 || host->Size < sizeof(HostApi)) return InvalidArgument;
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            if (_dispatcher is null) return Unexpected;
            _packageRoot = new string(root, 0, rootLength);
            using var ui = MusicUiContext.Enter();
            _host = *host;
            HostConfig.Initialize(host->GetConfigJson);
            PackageLog.Sink = Log;
            if (host->SetConfigChangedHandler != 0)
            {
                int status = ((delegate* unmanaged[Cdecl]<nint, nint, int>)host->SetConfigChangedHandler)(
                    host->Context, (nint)(delegate* unmanaged[Cdecl]<void>)&ConfigChanged);
                if (status != 0) { Reset(); return status; }
            }
            _active = true;
            _generation++;
            Log("[MusicPackage] activated ABI=4");
            return 0;
        }
        catch (Exception error) { Reset(); return error.HResult; }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_create", CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateWidget(char* contribution, int contributionLength, char* instance, int instanceLength,
        char* dataRoot, int dataLength, nint* handle, nint* view)
    {
        if (handle is null || view is null) return InvalidArgument;
        *handle = 0; *view = 0;
        MusicWidgetController? controller = null;
        nint abi = 0;
        try
        {
            if (!_active || _dispatcher?.HasThreadAccess != true ||
                !ValidText(contribution, contributionLength) || !ValidText(instance, instanceLength) ||
                !ValidText(dataRoot, dataLength) || new string(contribution, 0, contributionLength) != "music")
                return InvalidArgument;
            using var ui = MusicUiContext.Enter();
            controller = new(_packageRoot, new string(instance, 0, instanceLength), new string(dataRoot, 0, dataLength));
            abi = WinRT.MarshalInspectable<FrameworkElement>.FromManaged(controller.View);
            nint id = checked((nint)(++_nextHandle));
            Instances.Add(id, controller);
            *handle = id; *view = abi;
            controller.Start();
            Log($"[MusicPackage] created instance={controller.InstanceId} handle={id} live={Instances.Count}");
            return 0;
        }
        catch (Exception error)
        {
            if (abi != 0) WinRT.MarshalInspectable<FrameworkElement>.DisposeAbi(abi);
            controller?.Dispose();
            Log($"[MusicPackage] create failed: {error}");
            return error.HResult;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static int DestroyWidget(nint handle)
    {
        try
        {
            if (_dispatcher?.HasThreadAccess != true) return Unexpected;
            if (!Instances.TryGetValue(handle, out var controller)) return InvalidHandle;
            using var ui = MusicUiContext.Enter();
            controller.Dispose();
            Instances.Remove(handle);
            Log($"[MusicPackage] destroyed instance={controller.InstanceId} live={Instances.Count}");
            return 0;
        }
        catch (Exception error) { Log(error.ToString()); return error.HResult; }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_event", CallConvs = [typeof(CallConvCdecl)])]
    public static int WidgetEvent(nint handle, DeskBoxWidgetEventV1* value)
    {
        try
        {
            if (_dispatcher?.HasThreadAccess != true) return Unexpected;
            if (value is null || value->Version != 1 || value->Size < sizeof(DeskBoxWidgetEventV1) ||
                value->Kind is < 1 or > 15 || !double.IsFinite(value->Width) || !double.IsFinite(value->Height))
                return InvalidArgument;
            if (!Instances.TryGetValue(handle, out var controller)) return InvalidHandle;
            using var ui = MusicUiContext.Enter();
            controller.OnLifecycleEvent(value->Kind, value->Width, value->Height, value->Flags);
            return 0;
        }
        catch (Exception error) { Log(error.ToString()); return error.HResult; }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_shutdown", CallConvs = [typeof(CallConvCdecl)])]
    public static int Shutdown()
    {
        try
        {
            if (Instances.Count != 0) return Unexpected;
            if (_host.SetConfigChangedHandler != 0)
                _ = ((delegate* unmanaged[Cdecl]<nint, nint, int>)_host.SetConfigChangedHandler)(_host.Context, 0);
            Log("[MusicPackage] shutdown live=0");
            Reset();
            return 0;
        }
        catch (Exception error) { Reset(); return error.HResult; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ConfigChanged()
    {
        try
        {
            int generation = _generation;
            _dispatcher?.TryEnqueue(() =>
            {
                if (!_active || generation != _generation) return;
                using var ui = MusicUiContext.Enter();
                foreach (var item in Instances.Values.ToArray())
                    try { item.ReloadSettings(); } catch (Exception error) { Log(error.ToString()); }
            });
        }
        catch (Exception error) { Log(error.ToString()); }
    }
    private static bool ValidText(char* text, int length) => text != null && length is > 0 and <= 32767
        && new ReadOnlySpan<char>(text, length).IndexOf('\0') < 0;
    private static void Log(string message)
    {
        try
        {
            if (_host.Log == 0) return;
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            fixed (byte* p = bytes) ((delegate* unmanaged[Cdecl]<byte*, int, void>)_host.Log)(p, bytes.Length);
        }
        catch { }
    }
    private static void Reset()
    {
        _active = false; _generation++; _host = default; _dispatcher = null;
        HostConfig.Reset(); PackageLog.Sink = null;
    }
}
