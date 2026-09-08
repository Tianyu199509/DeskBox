using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;

namespace DeskBox.Glance.NativeHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 3 || args[0] != "--development-package") return;
        string output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "stages.txt"), "Main\n");
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            File.AppendAllText(Path.Combine(output, "stages.txt"), "ComWrappers\n");
            Application.Start(initialization =>
            {
                File.AppendAllText(Path.Combine(output, "stages.txt"), "Application.Start callback\n");
                SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                _ = new ProbeApplication(Path.GetFullPath(args[1]), output);
            });
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString()); }
    }
}

public sealed partial class ProbeApplication : Application
{
    private Window? _window;
    private readonly string _package;
    private readonly string _output;
    internal ProbeApplication(string package, string output)
    {
        _package = package;
        _output = output;
        File.AppendAllText(Path.Combine(_output, "stages.txt"), "Application constructor\n");
        InitializeComponent();
        File.AppendAllText(Path.Combine(_output, "stages.txt"), "Resources\n");
        UnhandledException += (_, e) => File.WriteAllText(Path.Combine(_output, "unhandled.txt"), e.Exception.ToString());
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            File.AppendAllText(Path.Combine(_output, "stages.txt"), "OnLaunched\n");
            Load(_package);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(_output, "error.txt"), error.ToString());
            Exit();
        }
    }

    private unsafe void Load(string package)
    {
        // A local development-only ABI experiment, not the product plugin loader.
        // NativeAOT libraries are deliberately retained for process lifetime.
        nint module = NativeLibrary.Load(Path.Combine(package, "DeskBox.Glance.NativePackage.dll"));
        File.AppendAllText(Path.Combine(_output, "stages.txt"), "Module loaded\n");
        var create = (delegate* unmanaged[Cdecl]<char*, int, nint*, int>)NativeLibrary.GetExport(module, "glance_probe_create_view");
        var version = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(module, "glance_probe_version");
        var height = (delegate* unmanaged[Cdecl]<double, double>)NativeLibrary.GetExport(module, "glance_probe_panel_height");
        int packageVersion = version();
        double panelHeight = height(340);
        File.AppendAllText(Path.Combine(_output, "stages.txt"), "Business exports called\n");
        nint abi = 0;
        int status;
        fixed (char* directory = package) status = create(directory, package.Length, &abi);
        File.AppendAllText(Path.Combine(_output, "stages.txt"), $"CreateView status {status:X8}\n");
        if (status != 0 || abi == 0) throw new InvalidOperationException($"CreateView failed: 0x{status:X8}");
        FrameworkElement content;
        try { content = WinRT.MarshalInspectable<FrameworkElement>.FromAbi(abi); }
        finally { WinRT.MarshalInspectable<FrameworkElement>.DisposeAbi(abi); }
        content.Width = 440;
        content.Height = 560;
        _window = new Window { Title = "Glance native package probe", Content = content };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 610));
        content.Loaded += (sender, args) => _ = Capture(content, packageVersion, panelHeight);
        _window.Activate();
    }

    private async Task Capture(FrameworkElement content, int version, double panelHeight)
    {
        try
        {
            await Task.Delay(800);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(content);
            var pixels = await bitmap.GetPixelsAsync();
            byte[] bytes = new byte[pixels.Length];
            using (DataReader reader = DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_output);
            StorageFile file = await folder.CreateFileAsync("view.png", CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
            // Explicit projection keeps runtime-created controls usable under
            // trimming; FindName returns an inspectable, not a guaranteed CLR type.
            double actualCalendarHeight = content.FindName("Calendar").As<CalendarView>().ActualHeight;
            string heading = content.FindName("Heading").As<TextBlock>().Text;
            using var output = File.Create(Path.Combine(_output, "result.json"));
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteBoolean("dynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported);
            writer.WriteNumber("packageVersion", version);
            writer.WriteNumber("panelHeightFor340", panelHeight);
            writer.WriteNumber("calendarActualHeight", actualCalendarHeight);
            writer.WriteString("heading", heading);
            writer.WriteNumber("width", bitmap.PixelWidth);
            writer.WriteNumber("height", bitmap.PixelHeight);
            writer.WriteEndObject();
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(_output, "error.txt"), error.ToString()); }
        finally { _window?.Close(); Exit(); }
    }
}
