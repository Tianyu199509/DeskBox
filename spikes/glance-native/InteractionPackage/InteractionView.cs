using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace DeskBox.Interaction.NativePackage;

/// <summary>
/// Batch C2 host-contract probe (ABI v2 + HostApi v2): package-localized
/// strings (strings/&lt;locale&gt;.json), theme tokens MERGED into the view's
/// resources (never replacing the view dictionary), toolkit Segmented
/// subscribed on the control itself, keyboard input counted on the package
/// TextBox, and host config pushes (locale/theme) applied live.
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
    private static int _configChangedCount;
    private static delegate* unmanaged[Cdecl]<byte*, int, void> _hostLog;
    private static delegate* unmanaged[Cdecl]<byte*, int, int> _getConfigJson;
    private static delegate* unmanaged[Cdecl]<nint, int> _setConfigChangedHandler;
    private static nint _configChangedHandler;

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_get_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetAbiVersion() => 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct HostApiV1
    {
        public uint Size;
        public uint Version;
        public nint Log;
        public nint GetConfigJson;
        public nint SetConfigChangedHandler;
    }

    [UnmanagedCallersOnly(EntryPoint = "deskbox_package_activate", CallConvs = [typeof(CallConvCdecl)])]
    public static int Activate(char* packageRoot, int packageRootLength, char* packageDataRoot, int packageDataRootLength, HostApiV1* hostApi)
    {
        try
        {
            _packageRoot = new string(packageRoot, 0, packageRootLength);
            _packageDataRoot = new string(packageDataRoot, 0, packageDataRootLength);
            Directory.CreateDirectory(_packageDataRoot);
            if (hostApi is not null && hostApi->Version >= 2 && hostApi->Log != 0)
            {
                _hostLog = (delegate* unmanaged[Cdecl]<byte*, int, void>)hostApi->Log;
                _getConfigJson = (delegate* unmanaged[Cdecl]<byte*, int, int>)hostApi->GetConfigJson;
                _setConfigChangedHandler = (delegate* unmanaged[Cdecl]<nint, int>)hostApi->SetConfigChangedHandler;
                // Register the package-side config-changed callback with the host.
                _setConfigChangedHandler((nint)(delegate* unmanaged[Cdecl]<byte*, int, void>)&OnConfigChanged);
                HostLog("interaction package activated (abi 2, hostapi 2)");
            }
            else if (hostApi is not null && hostApi->Log != 0)
            {
                _hostLog = (delegate* unmanaged[Cdecl]<byte*, int, void>)hostApi->Log;
                HostLog("interaction package activated (abi 2, hostapi 1)");
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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnConfigChanged(byte* utf8, int length)
    {
        _configChangedCount++;
        string json = Encoding.UTF8.GetString(utf8, length);
        try
        {
            HostLog("config changed: " + json);
            foreach (InteractionInstance instance in Instances.Values.ToList())
            {
                instance.ApplyConfig(json);
            }
        }
        catch (Exception error)
        {
            WriteDiagnostics("config-changed-error.txt", error.ToString());
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
            string? config = GetHostConfig();
            var created = InteractionInstance.Create(_packageRoot, contribution, instance, dataRoot, config);
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
            writer.WriteNumber("configChangedCount", _configChangedCount);
            writer.WriteString("lastAppliedAccent", InteractionInstance.LastAppliedAccent);
            writer.WriteNumber("keyDownCount", InteractionInstance.KeyDownCount);
            writer.WriteNumber("segmentedSelectionChanges", InteractionInstance.SegmentedSelectionChanges);
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

    private static string? GetHostConfig()
    {
        if (_getConfigJson is null) return null;
        byte[] buffer = new byte[512];
        int needed;
        fixed (byte* pointer = buffer)
        {
            needed = _getConfigJson(pointer, buffer.Length);
        }
        if (needed <= 0 || needed > buffer.Length) return null;
        return Encoding.UTF8.GetString(buffer, 0, needed);
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
    public static int KeyDownCount;
    public static int SegmentedSelectionChanges;

    private readonly string _packageRoot;
    private readonly string _instanceDataRoot;
    private readonly List<string> _items;
    private Dictionary<string, string> _strings = [];
    private string _locale = "zh-CN";
    private string _accent = "#FF4CC2FF";

    public string ContributionId { get; }
    public string InstanceId { get; }
    public int AddClicks;
    public static string LastAppliedAccent = "";

    private FrameworkElement? _view;

    private InteractionInstance(string packageRoot, string contributionId, string instanceId, string instanceDataRoot, List<string> items)
    {
        _packageRoot = packageRoot;
        ContributionId = contributionId;
        InstanceId = instanceId;
        _instanceDataRoot = instanceDataRoot;
        _items = items;
    }

    public static InteractionInstance Create(string packageRoot, string contributionId, string instanceId, string instanceDataRoot, string? hostConfigJson)
    {
        Directory.CreateDirectory(instanceDataRoot);
        var instance = new InteractionInstance(packageRoot, contributionId, instanceId, instanceDataRoot, LoadItems(instanceDataRoot));
        instance.ApplyConfig(hostConfigJson);
        return instance;
    }

    /// <summary>Config JSON: {"locale":"zh-CN","accent":"#FF..."}. Applies strings + theme.</summary>
    public void ApplyConfig(string? configJson)
    {
        if (!string.IsNullOrEmpty(configJson))
        {
            using JsonDocument document = JsonDocument.Parse(configJson);
            if (document.RootElement.TryGetProperty("locale", out JsonElement locale))
                _locale = locale.GetString() ?? _locale;
            if (document.RootElement.TryGetProperty("accent", out JsonElement accent))
                _accent = accent.GetString() ?? _accent;
        }
        _strings = LoadStrings(_packageRoot, _locale);
        if (_view is not null) ApplyToView(_view);
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
        addButton.Click += (_, _) =>
        {
            string text = input.Text.Trim();
            if (text.Length == 0) { status.Text = "empty input ignored"; return; }
            _items.Add(text);
            list.Items.Add(text);
            input.Text = string.Empty;
            Save();
            AddClicks++;
            status.Text = $"{ContributionId}/{InstanceId}: {text}";
        };
        // Keyboard contract: the package's own KeyDown handler on its TextBox.
        input.KeyDown += (_, _) => KeyDownCount++;
        // Toolkit contract: Segmented must be subscribed on the control itself;
        // subscribing via the base Selector misses the event (batch C finding).
        var segmented = content.FindName("FilterSegmented");
        if (segmented is CommunityToolkit.WinUI.Controls.Segmented toolkitSegmented)
        {
            toolkitSegmented.SelectionChanged += (_, _) => SegmentedSelectionChanges++;
        }
        ApplyToView(content);
        _view = content;
        return content;
    }

    private void ApplyToView(FrameworkElement content)
    {
        // Localization: pre-localized bindable properties (no x:Uid in runtime XAML).
        if (content.FindName("TitleText")?.As<TextBlock>() is { } title)
            title.Text = _strings.GetValueOrDefault("title", "DeskBox");
        if (content.FindName("AddButton")?.As<Button>() is { } add)
            add.Content = _strings.GetValueOrDefault("add", "Add");
        if (content.FindName("InputBox")?.As<TextBox>() is { } input)
            input.Header = _strings.GetValueOrDefault("todoHeader", "Todo");
        // Theme: set the probe element directly - WinRT resolved ThemeResource
        // references are static; changing a dictionary entry post-load does not
        // update them. The contract finding: packages own their themeable
        // elements and apply tokens by property set, not dictionary mutation.
        if (content.FindName("ThemeProbe")?.As<Microsoft.UI.Xaml.Controls.Border>() is { } probe)
        {
            probe.Background = new SolidColorBrush(ParseColor(_accent));
        }
        LastAppliedAccent = _accent;
    }

    internal static Dictionary<string, string> LoadStrings(string packageRoot, string locale)
    {
        string path = Path.Combine(packageRoot, "strings", $"{locale}.json");
        if (!File.Exists(path))
        {
            path = Path.Combine(packageRoot, "strings", "en-US.json");
            if (!File.Exists(path)) return [];
        }
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name, property => property.Value.GetString() ?? "");
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        return new Windows.UI.Color
        {
            A = Convert.ToByte(hex.Substring(1, 2), 16),
            R = Convert.ToByte(hex.Substring(3, 2), 16),
            G = Convert.ToByte(hex.Substring(5, 2), 16),
            B = Convert.ToByte(hex.Substring(7, 2), 16),
        };
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
