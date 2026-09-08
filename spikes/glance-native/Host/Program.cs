using System.Diagnostics;
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

internal enum Scenario
{
    Simple,
    RealGlance,
    Lifecycle,
    MultiPackage,
    TodoEdit,
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Scenario scenario;
        string[] packages;
        string output;
        switch (args)
        {
            case ["--development-package", string package, string outDir]:
                (scenario, packages, output) = (Scenario.Simple, [package], outDir);
                break;
            case ["--real-package", string package, string outDir]:
                (scenario, packages, output) = (Scenario.RealGlance, [package], outDir);
                break;
            case ["--lifecycle-package", string package, string outDir]:
                (scenario, packages, output) = (Scenario.Lifecycle, [package], outDir);
                break;
            case ["--multi-package", string packageA, string packageB, string outDir]:
                (scenario, packages, output) = (Scenario.MultiPackage, [packageA, packageB], outDir);
                break;
            case ["--todo-package", string package, string outDir]:
                (scenario, packages, output) = (Scenario.TodoEdit, [package], outDir);
                break;
            default:
                return;
        }
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "stages.txt"), "Main\n");
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                _ = new ProbeApplication(scenario, packages.Select(Path.GetFullPath).ToArray(), output);
            });
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString()); }
    }
}

public sealed partial class ProbeApplication : Application
{
    private static unsafe bool TryCreateViewExport(
        nint module,
        string name,
        out delegate* unmanaged[Cdecl]<char*, int, nint*, int> export)
    {
        try
        {
            export = (delegate* unmanaged[Cdecl]<char*, int, nint*, int>)NativeLibrary.GetExport(module, name);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            export = null;
            return false;
        }
    }

    private Window? _window;
    private readonly Scenario _scenario;
    private readonly string[] _packages;
    private readonly string _output;

    internal ProbeApplication(Scenario scenario, string[] packages, string output)
    {
        _scenario = scenario;
        _packages = packages;
        _output = output;
        File.AppendAllText(Path.Combine(_output, "stages.txt"), "Application constructor\n");
        InitializeComponent();
        UnhandledException += (_, e) => File.WriteAllText(Path.Combine(_output, "unhandled.txt"), e.Exception.ToString());
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Run();
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(_output, "error.txt"), error.ToString());
            Exit();
        }
    }

    private static nint LoadModule(string package, string dllName, string output)
    {
        // A local development-only ABI experiment, not the product plugin loader.
        // NativeAOT libraries are deliberately retained for process lifetime.
        nint module = NativeLibrary.Load(Path.Combine(package, dllName));
        File.AppendAllText(Path.Combine(output, "stages.txt"), $"Module loaded: {dllName}\n");
        return module;
    }

    private static unsafe FrameworkElement CreateView(nint module, string export, string package, string output)
    {
        if (!TryCreateViewExport(module, export, out var create))
        {
            throw new InvalidOperationException($"export missing: {export}");
        }
        nint abi = 0;
        int status;
        fixed (char* directory = package) status = create(directory, package.Length, &abi);
        File.AppendAllText(Path.Combine(output, "stages.txt"), $"{export} status {status:X8}\n");
        if (status != 0 || abi == 0) throw new InvalidOperationException($"{export} failed: 0x{status:X8}");
        try { return WinRT.MarshalInspectable<FrameworkElement>.FromAbi(abi); }
        finally { WinRT.MarshalInspectable<FrameworkElement>.DisposeAbi(abi); }
    }

    private void Run()
    {
        switch (_scenario)
        {
            case Scenario.Simple: RunSimple(); break;
            case Scenario.RealGlance: RunReal(); break;
            case Scenario.Lifecycle: RunLifecycle(); break;
            case Scenario.MultiPackage: RunMulti(); break;
            case Scenario.TodoEdit: RunTodo(); break;
        }
    }

    private unsafe void RunSimple()
    {
        nint module = LoadModule(_packages[0], "DeskBox.Glance.NativePackage.dll", _output);
        var version = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(module, "glance_probe_version");
        var height = (delegate* unmanaged[Cdecl]<double, double>)NativeLibrary.GetExport(module, "glance_probe_panel_height");
        int packageVersion = version();
        double panelHeight = height(340);
        FrameworkElement content = CreateView(module, "glance_probe_create_view", _packages[0], _output);
        content.Width = 440;
        content.Height = 560;
        _window = new Window { Title = "Glance native package probe", Content = content };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 610));
        content.Loaded += (sender, args) => _ = FinishSimple(content, packageVersion, panelHeight);
        _window.Activate();
    }

    private async Task FinishSimple(FrameworkElement content, int version, double panelHeight)
    {
        try
        {
            await Task.Delay(800);
            await CaptureAsync(content, "view.png");
            double actualCalendarHeight = content.FindName("Calendar").As<CalendarView>().ActualHeight;
            string heading = content.FindName("Heading").As<TextBlock>().Text;
            WriteResult(result =>
            {
                result.WriteBoolean("dynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported);
                result.WriteNumber("packageVersion", version);
                result.WriteNumber("panelHeightFor340", panelHeight);
                result.WriteNumber("calendarActualHeight", actualCalendarHeight);
                result.WriteString("heading", heading);
            });
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(_output, "error.txt"), error.ToString()); }
        finally { _window?.Close(); Exit(); }
    }

    private void RunReal()
    {
        Stopwatch clock = Stopwatch.StartNew();
        nint module = LoadModule(_packages[0], "DeskBox.Glance.NativePackage.dll", _output);
        FrameworkElement content = CreateView(module, "glance_probe_create_real_view", _packages[0], _output);
        double moduleReadyMilliseconds = clock.ElapsedMilliseconds;
        content.Width = 440;
        content.Height = 560;
        _window = new Window { Title = "Glance real slice probe", Content = content };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 610));
        content.Loaded += (sender, args) => _ = FinishReal(content, moduleReadyMilliseconds);
        _window.Activate();
    }

    private async Task FinishReal(FrameworkElement content, double moduleReadyMilliseconds)
    {
        try
        {
            await Task.Delay(900);
            await CaptureAsync(content, "view.png");
            // Explicit projection keeps runtime-created controls usable under
            // trimming; FindName returns an inspectable, not a guaranteed CLR type.
            var calendar = content.FindName("NativeCalendarView").As<CalendarView>();
            string title = content.FindName("TraditionalCalendarTitlePresenter").As<TextBlock>().Text;
            JsonDocument summary = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(_packages[0], "real-summary.json")));
            WriteResult(result =>
            {
                result.WriteBoolean("dynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported);
                result.WriteNumber("moduleReadyMilliseconds", moduleReadyMilliseconds);
                result.WriteNumber("calendarActualHeight", calendar.ActualHeight);
                result.WriteString("traditionalTitle", title);
                result.WritePropertyName("packageSummary");
                summary.RootElement.WriteTo(result);
            });
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(_output, "error.txt"), error.ToString()); }
        finally { _window?.Close(); Exit(); }
    }

    private unsafe void RunLifecycle()
    {
        nint module = LoadModule(_packages[0], "DeskBox.Glance.NativePackage.dll", _output);
        var version = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(module, "glance_probe_version");
        _ = RunLifecycleAsync(module, version());
    }

    private async Task RunLifecycleAsync(nint module, int version)
    {
        try
        {
            FrameworkElement first = CreateView(module, "glance_probe_create_view", _packages[0], _output);
            first.Width = 440;
            first.Height = 560;
            var firstUnloaded = new TaskCompletionSource();
            first.Unloaded += (_, _) => firstUnloaded.TrySetResult();
            _window = new Window { Title = "Lifecycle probe", Content = first };
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 610));
            await WhenLoadedAsync(first);
            bool firstLoaded = true;

            _window.Content = null;
            await firstUnloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            bool firstUnloadedFired = true;

            FrameworkElement second = CreateView(module, "glance_probe_create_view", _packages[0], _output);
            second.Width = 440;
            second.Height = 560;
            _window.Content = second;
            await WhenLoadedAsync(second);
            await Task.Delay(700);
            await CaptureAsync(second, "view.png");
            double actualCalendarHeight = second.FindName("Calendar").As<CalendarView>().ActualHeight;
            string heading = second.FindName("Heading").As<TextBlock>().Text;
            WriteResult(result =>
            {
                result.WriteNumber("packageVersion", version);
                result.WriteBoolean("firstLoaded", firstLoaded);
                result.WriteBoolean("firstUnloaded", firstUnloadedFired);
                result.WriteBoolean("secondLoaded", true);
                result.WriteNumber("secondCalendarActualHeight", actualCalendarHeight);
                result.WriteString("secondHeading", heading);
            });
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(_output, "error.txt"), error.ToString()); }
        finally { _window?.Close(); Exit(); }
    }

    private static Task WhenLoadedAsync(FrameworkElement element)
    {
        if (element.IsLoaded) return Task.CompletedTask;
        TaskCompletionSource loaded = new();
        element.Loaded += (_, _) => loaded.TrySetResult();
        return loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private unsafe void RunMulti()
    {
        nint moduleA = LoadModule(_packages[0], "DeskBox.Glance.NativePackage.dll", _output);
        nint moduleB = LoadModule(_packages[1], "DeskBox.Glance.NativePackage.dll", _output);
        if (moduleA == moduleB) throw new InvalidOperationException("expected two distinct modules");
        var versionA = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(moduleA, "glance_probe_version");
        FrameworkElement viewA = CreateView(moduleA, "glance_probe_create_view", _packages[0], _output);
        FrameworkElement viewB = CreateView(moduleB, "glance_probe_create_real_view", _packages[1], _output);
        viewA.Width = 440;
        viewA.Height = 560;
        viewB.Width = 440;
        viewB.Height = 560;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        panel.Children.Add(viewA);
        panel.Children.Add(viewB);
        panel.Background = null;
        var scroll = new ScrollViewer { Content = panel };
        _window = new Window { Title = "Multi-package probe", Content = scroll };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 620));
        panel.Loaded += (sender, args) => _ = FinishMulti(viewA, viewB, versionA());
    }

    private async Task FinishMulti(FrameworkElement viewA, FrameworkElement viewB, int versionA)
    {
        try
        {
            await Task.Delay(900);
            await CaptureAsync(viewB, "view.png");
            double simpleHeight = viewA.FindName("Calendar").As<CalendarView>().ActualHeight;
            string realTitle = viewB.FindName("TraditionalCalendarTitlePresenter").As<TextBlock>().Text;
            JsonDocument summary = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(_packages[1], "real-summary.json")));
            WriteResult(result =>
            {
                result.WriteBoolean("dynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported);
                result.WriteNumber("simplePackageVersion", versionA);
                result.WriteNumber("simpleCalendarActualHeight", simpleHeight);
                result.WriteString("realTraditionalTitle", realTitle);
                result.WritePropertyName("realPackageSummary");
                summary.RootElement.WriteTo(result);
            });
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(_output, "error.txt"), error.ToString()); }
        finally { _window?.Close(); Exit(); }
    }

    private void RunTodo()
    {
        nint module = LoadModule(_packages[0], "DeskBox.Todo.NativePackage.dll", _output);
        FrameworkElement editor = CreateView(module, "todo_probe_create_editor", _packages[0], _output);
        editor.Width = 360;
        editor.Height = 420;
        _window = new Window { Title = "Todo edit probe", Content = editor };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(380, 470));
        editor.Loaded += (sender, args) => _ = FinishTodo(module);
        _window.Activate();
    }

    private async Task FinishTodo(nint module)
    {
        try
        {
            FrameworkElement first = (FrameworkElement)_window!.Content!;
            var input = first.FindName("InputBox").As<TextBox>();
            var addButton = first.FindName("AddButton").As<Button>();
            var list = first.FindName("ItemsList").As<ListView>();
            const string sampleText = "采购牛奶（宿主代输入）";
            input.Text = sampleText;
            // Peers are created lazily; construct the automation peer directly and
            // invoke it, which routes through the same click path as a real tap.
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(addButton);
            peer.Invoke();
            await Task.Delay(400);
            int firstCount = list.Items.Count;

            var firstUnloaded = new TaskCompletionSource();
            first.Unloaded += (_, _) => firstUnloaded.TrySetResult();
            _window.Content = null;
            await firstUnloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));

            FrameworkElement second = CreateView(module, "todo_probe_create_editor", _packages[0], _output);
            second.Width = 360;
            second.Height = 420;
            _window.Content = second;
            await WhenLoadedAsync(second);
            var secondList = second.FindName("ItemsList").As<ListView>();
            await Task.Delay(400);
            int secondCount = secondList.Items.Count;
            string persistedItem = secondList.Items.Count > 0 ? secondList.Items[0]!.ToString() ?? "" : "";
            await CaptureAsync(second, "view.png");
            WriteResult(result =>
            {
                result.WriteBoolean("dynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported);
                result.WriteNumber("itemsAfterFirstEdit", firstCount);
                result.WriteNumber("itemsAfterRecreate", secondCount);
                result.WriteString("persistedItem", persistedItem);
            });
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(_output, "error.txt"), error.ToString()); }
        finally { _window?.Close(); Exit(); }
    }

    private async Task CaptureAsync(FrameworkElement content, string fileName)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(content);
        var pixels = await bitmap.GetPixelsAsync();
        byte[] bytes = new byte[pixels.Length];
        using (DataReader reader = DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_output);
        StorageFile file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }

    private void WriteResult(Action<Utf8JsonWriter> write)
    {
        using var stream = File.Create(Path.Combine(_output, "result.json"));
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        write(writer);
        writer.WriteEndObject();
    }
}
