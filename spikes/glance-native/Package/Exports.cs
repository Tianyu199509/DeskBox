using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace DeskBox.Glance.NativePackage;

public static unsafe class Exports
{
#if GLANCE_VERSION_TWO
    private const int Version = 2;
#else
    private const int Version = 1;
#endif
    [UnmanagedCallersOnly(EntryPoint = "glance_probe_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetVersion() => Version;

    [UnmanagedCallersOnly(EntryPoint = "glance_probe_panel_height", CallConvs = [typeof(CallConvCdecl)])]
    public static double PanelHeight(double height) =>
        GlanceCalendarLayoutCalculator.CalculatePanelHeight(height, GlanceCalendarLayoutCalculator.IsCompact(height), false);

    [UnmanagedCallersOnly(EntryPoint = "glance_probe_create_view", CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateView(char* directory, int length, nint* view)
    {
        if (directory is null || length is <= 0 or > 32767 || view is null) return -1;
        *view = 0;
        try
        {
            string root = new(directory, 0, length);
            var content = (FrameworkElement)XamlReader.Load(File.ReadAllText(Path.Combine(root, "calendar.xaml")));
            content.DataContext = new GlancePresentation
            {
                Title = $"Glance native package v{Version}",
                PanelHeight = GlanceCalendarLayoutCalculator.CalculatePanelHeight(340,
                    GlanceCalendarLayoutCalculator.IsCompact(340), false)
            };
            *view = WinRT.MarshalInspectable<FrameworkElement>.FromManaged(content);
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(new string(directory, 0, length), "activation-error.txt"), error.ToString());
            return error.HResult;
        }
    }
    [UnmanagedCallersOnly(EntryPoint = "glance_probe_create_real_view", CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateRealView(char* directory, int length, nint* view)
    {
        if (directory is null || length is <= 0 or > 32767 || view is null) return -1;
        *view = 0;
        string root = new(directory, 0, length);
        try
        {
            FrameworkElement content = RealGlanceView.Create(root);
            *view = WinRT.MarshalInspectable<FrameworkElement>.FromManaged(content);
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "activation-error.txt"), error.ToString());
            return error.HResult;
        }
    }
    [UnmanagedCallersOnly(EntryPoint = "glance_probe_create_compiled_view", CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateCompiledView(char* directory, int length, nint* view)
    {
        if (directory is null || length is <= 0 or > 32767 || view is null) return -1;
        *view = 0;
        string root = new(directory, 0, length);
        try
        {
            PackageContext.Root = root;
            LocalizationProbe.RunAsync(root).GetAwaiter().GetResult();
            // Discrimination step: a type-free compiled control first, so the
            // activation error names the failing layer (XBF locator vs type
            // resolution vs localization).
            var minimal = new MinimalControl();
            File.AppendAllText(Path.Combine(root, "compiled-stages.txt"), "minimal ok\n");
            var control = new RealGlanceControl();
            File.AppendAllText(Path.Combine(root, "compiled-stages.txt"), "real compiled ok\n");
            *view = WinRT.MarshalInspectable<FrameworkElement>.FromManaged(control);
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "activation-error.txt"), error.ToString());
            return error.HResult;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "glance_probe_create_full_view", CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateFullView(char* directory, int length, nint* view)
    {
        if (directory is null || length is <= 0 or > 32767 || view is null) return -1;
        *view = 0;
        string root = new(directory, 0, length);
        try
        {
            FrameworkElement content = FullGlanceView.Create(root);
            *view = WinRT.MarshalInspectable<FrameworkElement>.FromManaged(content);
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "activation-error.txt"), error.ToString());
            return error.HResult;
        }
    }

    // Unified package ABI v2 (batch C1 runtime contract): package lifecycle
    // (activate/shutdown) separated from widget-instance lifecycle (create/
    // destroy by opaque handle). Legacy glance_probe_* exports remain for the
    // spike harness.
    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_get_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetUnifiedAbiVersion() => 2;

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_activate", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int UnifiedActivate(char* packageRoot, int packageRootLength, char* packageDataRoot, int packageDataRootLength, UnifiedHostApi* hostApi)
    {
        if (packageRoot is null || packageDataRoot is null) return -1;
        UnifiedSession.PackageRoot = new string(packageRoot, 0, packageRootLength);
        UnifiedSession.DataRoot = new string(packageDataRoot, 0, packageDataRootLength);
        UnifiedSession.HostLog = hostApi is not null && hostApi->Log != 0
            ? (delegate* unmanaged[Cdecl]<byte*, int, void>)hostApi->Log
            : null;
        try
        {
            Directory.CreateDirectory(UnifiedSession.DataRoot);
            UnifiedSession.HostLogSafe("glance package activated (abi 2)");
            return 0;
        }
        catch (Exception error)
        {
            return error.HResult;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_create", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int UnifiedCreateWidget(char* contributionId, int contributionIdLength, char* instanceId, int instanceIdLength, char* instanceDataRoot, int instanceDataRootLength, nint* widgetHandle, nint* view)
    {
        if (widgetHandle is null || view is null) return -1;
        *widgetHandle = 0;
        *view = 0;
        try
        {
            Directory.CreateDirectory(new string(instanceDataRoot, 0, instanceDataRootLength));
            FrameworkElement content = RealGlanceView.Create(UnifiedSession.PackageRoot);
            nint handle = ++UnifiedSession.NextHandle;
            UnifiedSession.LiveInstances.Add(handle);
            *widgetHandle = handle;
            *view = WinRT.MarshalInspectable<FrameworkElement>.FromManaged(content);
            UnifiedSession.HostLogSafe($"glance widget created: {new string(contributionId, 0, contributionIdLength)}/{new string(instanceId, 0, instanceIdLength)}");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(UnifiedSession.DataRoot, "unified-activation-error.txt"), error.ToString());
            return error.HResult;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static int UnifiedDestroyWidget(nint widgetHandle) => UnifiedSession.LiveInstances.Remove(widgetHandle) ? 0 : 0;

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_shutdown", CallConvs = [typeof(CallConvCdecl)])]
    public static int UnifiedShutdown()
    {
        try
        {
            UnifiedSession.ShutdownCalls++;
            File.WriteAllText(Path.Combine(UnifiedSession.DataRoot, "unified-session.txt"),
                $"instancesRemaining={UnifiedSession.LiveInstances.Count} shutdowns={UnifiedSession.ShutdownCalls} time={DateTime.Now:O}");
            UnifiedSession.HostLogSafe("glance package shutdown");
            return 0;
        }
        catch
        {
            return 0;
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct UnifiedHostApi
{
    public uint Size;
    public uint Version;
    public nint Log;
    public nint GetConfigJson;
    public nint SetConfigChangedHandler;
}

internal unsafe static class UnifiedSession
{
    public static string PackageRoot = "";
    public static string DataRoot = "";
    public static nint NextHandle;
    public static int ShutdownCalls;
    public static readonly HashSet<nint> LiveInstances = [];
    public static delegate* unmanaged[Cdecl]<byte*, int, void> HostLog;

    public static unsafe void HostLogSafe(string message)
    {
        if (HostLog is null) return;
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(message);
        fixed (byte* pointer = utf8) HostLog(pointer, utf8.Length);
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GlancePresentation
{
    public string Title { get; init; } = "";
    public double PanelHeight { get; init; }
}

