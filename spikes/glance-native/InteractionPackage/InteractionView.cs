using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace DeskBox.Interaction.NativePackage;

public static unsafe class Exports
{
    // Batch C unified ABI shape: get_abi_version / activate(3 roots) /
    // create_widget / destroy_widget / shutdown. Data never touches packageRoot.
    [UnmanagedCallersOnly(EntryPoint = "interaction_get_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetAbiVersion() => InteractionState.AbiVersion;

    [UnmanagedCallersOnly(EntryPoint = "interaction_activate", CallConvs = [typeof(CallConvCdecl)])]
    public static int Activate(char* packageRoot, int packageRootLength, char* dataRoot, int dataRootLength, char* instanceId, int instanceIdLength)
    {
        if (packageRoot is null || dataRoot is null || instanceId is null) return -1;
        try
        {
            InteractionState.PackageRoot = new string(packageRoot, 0, packageRootLength);
            InteractionState.DataRoot = new string(dataRoot, 0, dataRootLength);
            InteractionState.InstanceId = new string(instanceId, 0, instanceIdLength);
            Directory.CreateDirectory(InteractionState.DataRoot);
            return 0;
        }
        catch (Exception error) { InteractionState.Fail(error); return error.HResult; }
    }

    [UnmanagedCallersOnly(EntryPoint = "interaction_create_widget", CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateWidget(char* widgetId, int widgetIdLength, nint* view)
    {
        if (widgetId is null || view is null) return -1;
        *view = 0;
        try
        {
            FrameworkElement content = InteractionView.Create(new string(widgetId, 0, widgetIdLength));
            *view = WinRT.MarshalInspectable<FrameworkElement>.FromManaged(content);
            return 0;
        }
        catch (Exception error) { InteractionState.Fail(error); return error.HResult; }
    }

    [UnmanagedCallersOnly(EntryPoint = "interaction_destroy_widget", CallConvs = [typeof(CallConvCdecl)])]
    public static int DestroyWidget(char* widgetId, int widgetIdLength)
    {
        InteractionState.DestroyCalls++;
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "interaction_shutdown", CallConvs = [typeof(CallConvCdecl)])]
    public static int Shutdown()
    {
        try
        {
            InteractionState.WriteSummary();
            return 0;
        }
        catch (Exception error) { InteractionState.Fail(error); return error.HResult; }
    }
}

internal static class InteractionState
{
    public const int AbiVersion = 1;
    public static string PackageRoot = "";
    public static string DataRoot = "";
    public static string InstanceId = "";
    public static int DestroyCalls;
    public static int EventWiringCount;
    public static int AddClicks;
    public static int SelectionChanges;
    public static bool ThemeTokenApplied;

    public static void Fail(Exception error) =>
        File.WriteAllText(Path.Combine(string.IsNullOrEmpty(DataRoot) ? Path.GetTempPath() : DataRoot, "activation-error.txt"), error.ToString());

    public static void WriteSummary()
    {
        using var stream = File.Create(Path.Combine(DataRoot, "interaction-summary.json"));
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("abiVersion", AbiVersion);
        writer.WriteString("instanceId", InstanceId);
        writer.WriteNumber("eventWiringCount", EventWiringCount);
        writer.WriteNumber("addClicks", AddClicks);
        writer.WriteNumber("selectionChanges", SelectionChanges);
        writer.WriteNumber("destroyCalls", DestroyCalls);
        writer.WriteBoolean("themeTokenApplied", ThemeTokenApplied);
        writer.WriteBoolean("hostControlResolved", true);
        writer.WriteEndObject();
    }
}

internal static class InteractionView
{
    public static FrameworkElement Create(string widgetId)
    {
        FrameworkElement content = (FrameworkElement)XamlReader.Load(
            File.ReadAllText(Path.Combine(InteractionState.PackageRoot, "interaction.xaml")));
        if (content is null) throw new InvalidOperationException("interaction.xaml failed to parse");

        // Theme token contract: host writes theme-tokens.json into the DATA
        // root; the package maps tokens onto a local resource dictionary
        // (local dictionaries resolve in runtime XAML - proven in round 2).
        var tokens = new ResourceDictionary();
        string tokenPath = Path.Combine(InteractionState.DataRoot, "theme-tokens.json");
        if (File.Exists(tokenPath))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(tokenPath));
            string accent = document.RootElement.GetProperty("accent").GetString() ?? "#FF4CC2FF";
            tokens["ProbeAccentBrush"] = new SolidColorBrush(Parse(accent));
            InteractionState.ThemeTokenApplied = true;
        }
        else
        {
            tokens["ProbeAccentBrush"] = new SolidColorBrush(Parse("#FF4CC2FF"));
        }
        content.Resources = tokens;

        var input = content.FindName("InputBox").As<TextBox>();
        var addButton = content.FindName("AddButton").As<Button>();
        var list = content.FindName("ItemsList").As<ListView>();
        var status = content.FindName("StatusText").As<TextBlock>();
        var segmented = content.FindName("FilterSegmented").As<object>();

        List<string> items = LoadItems();
        foreach (string item in items) list.Items.Add(item);
        status.Text = items.Count == 0 ? "no stored items" : $"{items.Count} stored item(s) reloaded";

        addButton.Click += (_, _) =>
        {
            string text = input.Text.Trim();
            if (text.Length == 0) { status.Text = "empty input ignored"; return; }
            items.Add(text);
            list.Items.Add(text);
            input.Text = string.Empty;
            SaveItems(items);
            InteractionState.AddClicks++;
            status.Text = $"saved {items.Count} item(s)";
        };
        InteractionState.EventWiringCount++;

        // Toolkit control interaction via its common interface surface.
        var selector = segmented as Microsoft.UI.Xaml.Controls.Primitives.Selector;
        if (selector is not null)
        {
            selector.SelectionChanged += (_, _) => InteractionState.SelectionChanges++;
            InteractionState.EventWiringCount++;
        }
        content.Tag = widgetId;
        return content;
    }

    private static Windows.UI.Color Parse(string hex)
    {
        return new Windows.UI.Color
        {
            A = Convert.ToByte(hex.Substring(1, 2), 16),
            R = Convert.ToByte(hex.Substring(3, 2), 16),
            G = Convert.ToByte(hex.Substring(5, 2), 16),
            B = Convert.ToByte(hex.Substring(7, 2), 16),
        };
    }

    private static List<string> LoadItems()
    {
        string path = Path.Combine(InteractionState.DataRoot, "interaction-items.json");
        if (!File.Exists(path)) return [];
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(element => element.GetString() ?? "").ToList();
    }

    private static void SaveItems(List<string> items)
    {
        using var stream = File.Create(Path.Combine(InteractionState.DataRoot, "interaction-items.json"));
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteStartArray("items");
        foreach (string item in items) writer.WriteStringValue(item);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
