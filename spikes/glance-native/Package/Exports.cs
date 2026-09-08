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

    // Unified package ABI (batch C shape) so the product pilot loader can drive
    // this package: get_abi_version / activate(3 roots) / create / destroy /
    // shutdown. Legacy glance_probe_* exports remain for the spike harness.
    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_get_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetUnifiedAbiVersion() => 1;

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_activate", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int UnifiedActivate(char* packageRoot, int packageRootLength, char* dataRoot, int dataRootLength, char* instanceId, int instanceIdLength)
    {
        if (packageRoot is null || dataRoot is null || instanceId is null) return -1;
        UnifiedSession.PackageRoot = new string(packageRoot, 0, packageRootLength);
        UnifiedSession.DataRoot = new string(dataRoot, 0, dataRootLength);
        UnifiedSession.InstanceId = new string(instanceId, 0, instanceIdLength);
        try
        {
            Directory.CreateDirectory(UnifiedSession.DataRoot);
            return 0;
        }
        catch (Exception error)
        {
            return error.HResult;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_create", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int UnifiedCreateWidget(char* widgetId, int widgetIdLength, nint* view)
    {
        if (view is null) return -1;
        *view = 0;
        try
        {
            FrameworkElement content = RealGlanceView.Create(UnifiedSession.PackageRoot);
            *view = WinRT.MarshalInspectable<FrameworkElement>.FromManaged(content);
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(UnifiedSession.DataRoot, "unified-activation-error.txt"), error.ToString());
            return error.HResult;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int UnifiedDestroyWidget(char* widgetId, int widgetIdLength) => 0;

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_shutdown", CallConvs = [typeof(CallConvCdecl)])]
    public static int UnifiedShutdown()
    {
        try
        {
            File.WriteAllText(Path.Combine(UnifiedSession.DataRoot, "unified-session.txt"),
                $"instance={UnifiedSession.InstanceId} shutdown={DateTime.Now:O}");
            return 0;
        }
        catch
        {
            return 0;
        }
    }
}

internal static class UnifiedSession
{
    public static string PackageRoot = "";
    public static string DataRoot = "";
    public static string InstanceId = "";
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GlancePresentation
{
    public string Title { get; init; } = "";
    public double PanelHeight { get; init; }
}

