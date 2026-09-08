using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace DeskBox.Interaction.NativePackage;

/// <summary>
/// Batch C1 runtime contract (ABI v2): package lifecycle (activate/shutdown)
/// strictly separated from widget-instance lifecycle (create/destroy by opaque
/// handle). Each instance owns its state and persists exclusively under its
/// instance data root; nothing is ever written to the package root.
/// </summary>
public static unsafe class Exports
{
    private static readonly Dictionary<nint, InteractionInstance> Instances = [];
    private static nint _nextHandle = 0x1000;
    private static string _packageRoot = "";
    private static string _packageDataRoot = "";
    private static int _activateCalls;
    private static int _shutdownCalls;
    private static int _hostLogCalls;
    private static delegate* unmanaged[Cdecl]<byte*, int, void> _hostLog;

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_get_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetAbiVersion() => 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct HostApiV1
    {
        public nint Log;
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_activate", CallConvs = [typeof(CallConvCdecl)])]
    public static int Activate(char* packageRoot, int packageRootLength, char* packageDataRoot, int packageDataRootLength, HostApiV1* hostApi)
    {
        try
        {
            _packageRoot = new string(packageRoot, 0, packageRootLength);
            _packageDataRoot = new string(packageDataRoot, 0, packageDataRootLength);
            Directory.CreateDirectory(_packageDataRoot);
            if (hostApi is not null && hostApi->Log != 0)
            {
                _hostLog = (delegate* unmanaged[Cdecl]<byte*, int, void>)hostApi->Log;
                HostLog("interaction package activated (abi 2)");
            }
            _activateCalls++;
            return 0;
        }
        catch (Exception error)
        {
            WriteDiagnostics("activate-error.txt", error.ToString());
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
            var created = InteractionInstance.Create(_packageRoot, contribution, instance, dataRoot);
            nint handle = ++_nextHandle;
            Instances[handle] = created;
            *widgetHandle = handle;
            *view = WinRT.MarshalInspectable<FrameworkElement>.FromManaged(created.BuildView());
            HostLog($"widget created: {contribution}/{instance}");
            return 0;
        }
        catch (Exception error)
        {
            WriteDiagnostics("create-error.txt", error.ToString());
            return error.HResult;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_widget_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static int DestroyWidget(nint widgetHandle)
    {
        if (Instances.Remove(widgetHandle))
        {
            HostLog($"widget destroyed: 0x{widgetHandle:X}");
        }
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_shutdown", CallConvs = [typeof(CallConvCdecl)])]
    public static int Shutdown()
    {
        _shutdownCalls++;
        try
        {
            using var stream = File.Create(Path.Combine(_packageDataRoot, "runtime-contract-summary.json"));
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteNumber("abiVersion", 2);
            writer.WriteNumber("activateCalls", _activateCalls);
            writer.WriteNumber("shutdownCalls", _shutdownCalls);
            writer.WriteNumber("hostLogCalls", _hostLogCalls);
            writer.WriteNumber("instancesCreatedTotal", _nextHandle - 0x1000);
            writer.WriteNumber("liveInstancesAfterShutdown", Instances.Count);
            writer.WriteEndObject();
            HostLog("interaction package shutdown");
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void HostLog(string message)
    {
        if (_hostLog is null) return;
        byte[] utf8 = Encoding.UTF8.GetBytes(message);
        fixed (byte* pointer = utf8) _hostLog(pointer, utf8.Length);
        _hostLogCalls++;
    }

    private static void WriteDiagnostics(string fileName, string content)
    {
        try
        {
            string root = string.IsNullOrEmpty(_packageDataRoot) ? Path.GetTempPath() : _packageDataRoot;
            File.WriteAllText(Path.Combine(root, fileName), content);
        }
        catch
        {
            // Diagnostics only; never fail an export for logging.
        }
    }
}

/// <summary>Per-instance state; persistence is confined to the instance data root.</summary>
internal sealed class InteractionInstance
{
    private readonly string _packageRoot;
    private readonly string _instanceDataRoot;
    private readonly List<string> _items;

    public string ContributionId { get; }
    public string InstanceId { get; }
    public int AddClicks;

    private InteractionInstance(string packageRoot, string contributionId, string instanceId, string instanceDataRoot, List<string> items)
    {
        _packageRoot = packageRoot;
        ContributionId = contributionId;
        InstanceId = instanceId;
        _instanceDataRoot = instanceDataRoot;
        _items = items;
    }

    public static InteractionInstance Create(string packageRoot, string contributionId, string instanceId, string instanceDataRoot)
    {
        Directory.CreateDirectory(instanceDataRoot);
        return new InteractionInstance(packageRoot, contributionId, instanceId, instanceDataRoot, LoadItems(instanceDataRoot));
    }

    public FrameworkElement BuildView()
    {
        FrameworkElement content = (FrameworkElement)XamlReader.Load(
            File.ReadAllText(Path.Combine(_packageRoot, "interaction.xaml")));
        var input = content.FindName("InputBox").As<TextBox>();
        var addButton = content.FindName("AddButton").As<Button>();
        var list = content.FindName("ItemsList").As<ListView>();
        var status = content.FindName("StatusText").As<TextBlock>();
        foreach (string item in _items) list.Items.Add(item);
        status.Text = _items.Count == 0 ? "no stored items" : $"{_items.Count} stored item(s) reloaded";
        addButton.Click += (_, _) =>
        {
            string text = input.Text.Trim();
            if (text.Length == 0) { status.Text = "empty input ignored"; return; }
            _items.Add(text);
            list.Items.Add(text);
            input.Text = string.Empty;
            Save();
            AddClicks++;
            status.Text = $"{ContributionId}/{InstanceId}: saved {_items.Count} item(s)";
        };
        return content;
    }

    private static List<string> LoadItems(string instanceDataRoot)
    {
        string path = Path.Combine(instanceDataRoot, "items.json");
        if (!File.Exists(path)) return [];
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(element => element.GetString() ?? "").ToList();
    }

    private void Save()
    {
        using var stream = File.Create(Path.Combine(_instanceDataRoot, "items.json"));
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteStartArray("items");
        foreach (string item in _items) writer.WriteStringValue(item);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
