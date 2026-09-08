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
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GlancePresentation
{
    public string Title { get; init; } = "";
    public double PanelHeight { get; init; }
}

