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
    CompiledGlance,
    FullGlance,
    Lifecycle,
    MultiPackage,
    TodoEdit,
    Interaction,
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
            case ["--compiled-package", string package, string outDir]:
                (scenario, packages, output) = (Scenario.CompiledGlance, [package], outDir);
                break;
            case ["--full-package", string package, string outDir]:
                (scenario, packages, output) = (Scenario.FullGlance, [package], outDir);
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
            case ["--interaction-package", string package, string outDir]:
                (scenario, packages, output) = (Scenario.Interaction, [package], outDir);
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
            case Scenario.CompiledGlance: RunCompiled(); break;
            case Scenario.FullGlance: RunFull(); break;
            case Scenario.Lifecycle: RunLifecycle(); break;
            case Scenario.MultiPackage: RunMulti(); break;
            case Scenario.TodoEdit: RunTodo(); break;
            case Scenario.Interaction: RunInteraction(); break;
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

    private unsafe void RunCompiled()
    {
        nint module = LoadModule(_packages[0], "DeskBox.Glance.NativePackage.dll", _output);
        bool activationFactoryExport = NativeLibrary.TryGetExport(module, "DllGetActivationFactory", out _);
        File.AppendAllText(Path.Combine(_output, "stages.txt"),
            $"DllGetActivationFactory present: {activationFactoryExport}\n");
        FrameworkElement? content = null;
        string failure = "";
        try
        {
            content = CreateView(module, "glance_probe_create_compiled_view", _packages[0], _output);
        }
        catch (Exception error)
        {
            // Pinned negative: XBF LoadComponent cannot locate compiled XAML in a
            // dynamically loaded AOT DLL (even a type-free minimal control). If a
            // future Windows App SDK loads it, this probe flips and the script
            // forces the spike contract findings to be re-evaluated.
            failure = error.Message;
        }
        if (content is null)
        {
            string stages = File.Exists(Path.Combine(_packages[0], "compiled-stages.txt"))
                ? File.ReadAllText(Path.Combine(_packages[0], "compiled-stages.txt")).Trim()
                : "none";
            string localization = File.Exists(Path.Combine(_packages[0], "localization-probe.json"))
                ? File.ReadAllText(Path.Combine(_packages[0], "localization-probe.json"))
                : "{}";
            JsonDocument localizationProbe = JsonDocument.Parse(localization);
            WriteResult(result =>
            {
                result.WriteBoolean("compiledXamlLoads", false);
                result.WriteString("compiledStages", stages);
                result.WriteBoolean("activationFactoryExport", activationFactoryExport);
                result.WriteString("failure", failure);
                result.WritePropertyName("localizationProbe");
                localizationProbe.RootElement.WriteTo(result);
            });
            Exit();
            return;
        }
        content.Width = 440;
        content.Height = 560;
        _window = new Window { Title = "Glance compiled slice probe", Content = content };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 610));
        content.Loaded += (sender, args) => _ = FinishCompiled(content, activationFactoryExport);
        _window.Activate();
    }

    private async Task FinishCompiled(FrameworkElement content, bool activationFactoryExport)
    {
        try
        {
            await Task.Delay(900);
            await CaptureAsync(content, "view.png");
            var calendar = content.FindName("NativeCalendarView").As<CalendarView>();
            var themeProbe = content.FindName("ThemeProbeText").As<TextBlock>();
            bool themeForegroundResolved = themeProbe.Foreground is not null;
            string title = content.FindName("TraditionalCalendarTitlePresenter").As<TextBlock>().Text;
            JsonDocument summary = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(_packages[0], "compiled-summary.json")));
            string localization = File.Exists(Path.Combine(_packages[0], "localization-probe.json"))
                ? await File.ReadAllTextAsync(Path.Combine(_packages[0], "localization-probe.json"))
                : "{}";
            JsonDocument localizationProbe = JsonDocument.Parse(localization);
            WriteResult(result =>
            {
                result.WriteBoolean("dynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported);
                result.WriteBoolean("activationFactoryExport", activationFactoryExport);
                result.WriteBoolean("themeForegroundResolved", themeForegroundResolved);
                result.WriteNumber("calendarActualHeight", calendar.ActualHeight);
                result.WriteString("traditionalTitle", title);
                result.WritePropertyName("packageSummary");
                summary.RootElement.WriteTo(result);
                result.WritePropertyName("localizationProbe");
                localizationProbe.RootElement.WriteTo(result);
            });
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(_output, "error.txt"), error.ToString()); }
        finally { _window?.Close(); Exit(); }
    }

    private unsafe void RunFull()
    {
        nint module = LoadModule(_packages[0], "DeskBox.Glance.NativePackage.dll", _output);
        FrameworkElement content = CreateView(module, "glance_probe_create_full_view", _packages[0], _output);
        content.Width = 440;
        content.Height = 560;
        _window = new Window { Title = "Glance full slice probe", Content = content };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 610));
        content.Loaded += (sender, args) => _ = FinishFull(module, content);
        _window.Activate();
    }

    private async Task FinishFull(nint module, FrameworkElement first)
    {
        try
        {
            // Stage 1: the package's own rotation timer advances the background.
            await Task.Delay(4000);
            JsonDocument stage1 = await ReadSummary("full-summary.json");
            int rotationIndex = stage1.RootElement.GetProperty("currentImageIndex").GetInt32();
            int revision1 = stage1.RootElement.GetProperty("revision").GetInt32();

            // Stage 2: a real click on the action bar's next button; wait for the
            // package's summary revision to advance so the read is not stale.
            var nextButton = first.FindName("NextButton").As<Button>();
            new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(nextButton).Invoke();
            JsonDocument stage2 = await ReadSummaryAfterRevision(revision1);
            int clickedIndex = stage2.RootElement.GetProperty("currentImageIndex").GetInt32();

            // Stage 3: programmatic toggle drives the package's Toggled handler.
            int revision2 = stage2.RootElement.GetProperty("revision").GetInt32();
            var festivalToggle = first.FindName("FestivalToggle").As<ToggleSwitch>();
            festivalToggle.IsOn = false;
            JsonDocument stage3 = await ReadSummaryAfterRevision(revision2);

            // Stage 4: destroy and recreate; settings must persist.
            var firstUnloaded = new TaskCompletionSource();
            first.Unloaded += (_, _) => firstUnloaded.TrySetResult();
            _window!.Content = null;
            await firstUnloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            FrameworkElement second = CreateView(module, "glance_probe_create_full_view", _packages[0], _output);
            second.Width = 440;
            second.Height = 560;
            _window.Content = second;
            await WhenLoadedAsync(second);
            await Task.Delay(600);
            JsonDocument stage4 = await ReadSummary("full-summary.json");
            await CaptureAsync(second, "view.png");

            WriteResult(result =>
            {
                result.WriteBoolean("dynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported);
                result.WriteNumber("rotationIndexAfterTimer", rotationIndex);
                result.WriteNumber("indexAfterNextClick", clickedIndex);
                result.WritePropertyName("afterFestivalOff");
                stage3.RootElement.WriteTo(result);
                result.WritePropertyName("afterRecreate");
                stage4.RootElement.WriteTo(result);
            });
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(_output, "error.txt"), error.ToString()); }
        finally { _window?.Close(); Exit(); }
    }

    private async Task<JsonDocument> ReadSummaryAfterRevision(int previousRevision)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            JsonDocument document = await ReadSummary("full-summary.json");
            if (document.RootElement.TryGetProperty("revision", out JsonElement revision) &&
                revision.GetInt32() > previousRevision)
            {
                return document;
            }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("summary revision did not advance");
    }

    private async Task<JsonDocument> ReadSummary(string fileName)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                string path = Path.Combine(_packages[0], fileName);
                if (File.Exists(path))
                {
                    using FileStream stream = File.OpenRead(path);
                    return await JsonDocument.ParseAsync(stream);
                }
            }
            catch (IOException) { }
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"summary not available: {fileName}");
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct ContractHostApi
    {
        public nint Log;
    }

    private static int _hostLogCalls;

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void ContractHostLog(byte* utf8, int length)
    {
        Interlocked.Increment(ref _hostLogCalls);
        string message = System.Text.Encoding.UTF8.GetString(utf8, length);
        File.AppendAllText(Path.Combine(_contractOutput ?? ".", "host-api-log.txt"), message + "\n");
    }

    private static string? _contractOutput;

    private unsafe void RunInteraction()
    {
        nint module = LoadModule(_packages[0], "DeskBox.Interaction.NativePackage.dll", _output);
        _contractOutput = _output;
        string packageDataRoot = Path.Combine(_output, "package-data");
        Directory.CreateDirectory(packageDataRoot);
        var activate = (delegate* unmanaged[Cdecl]<char*, int, char*, int, ContractHostApi*, int>)NativeLibrary.GetExport(module, "deskbox_package_activate");
        var create = (delegate* unmanaged[Cdecl]<char*, int, char*, int, char*, int, nint*, nint*, int>)NativeLibrary.GetExport(module, "deskbox_widget_create");
        var destroy = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(module, "deskbox_widget_destroy");
        var shutdown = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(module, "deskbox_package_shutdown");
        var version = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(module, "deskbox_package_get_abi_version");
        int abiVersion = version();
        var hostApi = new ContractHostApi { Log = (nint)(delegate* unmanaged[Cdecl]<byte*, int, void>)&ContractHostLog };

        string rootA = Path.Combine(packageDataRoot, "instances", "instance-a");
        string rootB = Path.Combine(packageDataRoot, "instances", "instance-b");
        nint handleA = 0, handleB = 0, viewA = 0, viewB = 0;
        int status;
        fixed (char* package = _packages[0])
        fixed (char* data = packageDataRoot)
        {
            ContractHostApi* api = &hostApi;
            status = activate(package, _packages[0].Length, data, packageDataRoot.Length, api);
            if (status != 0) throw new InvalidOperationException($"activate failed: 0x{status:X8}");
        }
        fixed (char* contribution = "main")
        fixed (char* instanceA = "instance-a")
        fixed (char* instanceB = "instance-b")
        fixed (char* dataA = rootA)
        fixed (char* dataB = rootB)
        {
            status = create(contribution, 4, instanceA, 10, dataA, rootA.Length, &handleA, &viewA);
            if (status != 0) throw new InvalidOperationException($"create A failed: 0x{status:X8}");
            status = create(contribution, 4, instanceB, 10, dataB, rootB.Length, &handleB, &viewB);
            if (status != 0) throw new InvalidOperationException($"create B failed: 0x{status:X8}");
        }
        FrameworkElement contentA = Project(viewA);
        FrameworkElement contentB = Project(viewB);
        nint capturedHandleA = handleA;
        nint capturedHandleB = handleB;
        contentA.Width = 420;
        contentA.Height = 230;
        contentB.Width = 420;
        contentB.Height = 230;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(contentA);
        panel.Children.Add(contentB);
        _window = new Window { Title = "Runtime contract probe", Content = panel };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(440, 520));
        panel.Loaded += (sender, args) => _ = FinishInteraction((nint)destroy, (nint)shutdown, contentA, contentB, capturedHandleA, capturedHandleB, packageDataRoot, rootA, rootB, abiVersion);
        _window.Activate();
    }

    private static FrameworkElement Project(nint abi)
    {
        try { return WinRT.MarshalInspectable<FrameworkElement>.FromAbi(abi); }
        finally { WinRT.MarshalInspectable<FrameworkElement>.DisposeAbi(abi); }
    }

    private static List<string> ReadItems(string instanceRoot)
    {
        string path = Path.Combine(instanceRoot, "items.json");
        if (!File.Exists(path)) return [];
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(element => element.GetString() ?? "").ToList();
    }

    private static unsafe void DestroyByHandle(nint destroyExport, nint handle) =>
        ((delegate* unmanaged[Cdecl]<nint, int>)destroyExport)(handle);

    private static unsafe void ShutdownPackage(nint shutdownExport) =>
        ((delegate* unmanaged[Cdecl]<int>)shutdownExport)();

    private async Task FinishInteraction(
        nint destroyExport,
        nint shutdownExport,
        FrameworkElement contentA,
        FrameworkElement contentB,
        nint handleA,
        nint handleB,
        string packageDataRoot,
        string rootA,
        string rootB,
        int abiVersion)
    {
        try
        {
            await WhenLoadedAsync(contentA);
            await WhenLoadedAsync(contentB);
            // Kept from the batch C probe: host custom control + toolkit control
            // must still resolve inside package runtime text XAML.
            bool hostControlResolved = contentA.FindName("HostBadgeSlot") is not null;
            bool toolkitControlResolved = contentA.FindName("FilterSegmented") is not null;

            // Instance A and instance B each add one item through real clicks paths.
            var inputA = contentA.FindName("InputBox").As<TextBox>();
            var addA = contentA.FindName("AddButton").As<Button>();
            inputA.Text = "条目甲";
            new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(addA).Invoke();
            var inputB = contentB.FindName("InputBox").As<TextBox>();
            var addB = contentB.FindName("AddButton").As<Button>();
            inputB.Text = "条目乙";
            new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(addB).Invoke();
            await Task.Delay(400);
            List<string> itemsA = ReadItems(rootA);
            List<string> itemsB = ReadItems(rootB);
            bool dataIsolated = itemsA is ["条目甲"] && itemsB is ["条目乙"];

            // Destroy A; B must remain fully functional.
            DestroyByHandle(destroyExport, handleA);
            inputB.Text = "条目乙二";
            new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(addB).Invoke();
            await Task.Delay(400);
            bool bSurvivedA = ReadItems(rootB) is ["条目乙", "条目乙二"];

            // Destroy B (last instance) -> package shutdown exactly once.
            DestroyByHandle(destroyExport, handleB);
            ShutdownPackage(shutdownExport);
            await CaptureAsync(contentB, "view.png");
            JsonDocument summary = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(packageDataRoot, "runtime-contract-summary.json")));
            bool packageRootUntouched = !File.Exists(Path.Combine(_packages[0], "items.json"));

            WriteResult(result =>
            {
                result.WriteNumber("abiVersion", abiVersion);
                result.WriteBoolean("hostControlResolved", hostControlResolved);
                result.WriteBoolean("toolkitControlResolved", toolkitControlResolved);
                result.WriteBoolean("dataIsolated", dataIsolated);
                result.WriteBoolean("bSurvivedADestroy", bSurvivedA);
                result.WriteBoolean("packageRootUntouched", packageRootUntouched);
                result.WriteNumber("hostLogCalls", _hostLogCalls);
                result.WritePropertyName("packageSummary");
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
