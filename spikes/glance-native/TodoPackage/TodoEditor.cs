using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using WinRT;

namespace DeskBox.Todo.NativePackage;

public static unsafe class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "todo_probe_create_editor", CallConvs = [typeof(CallConvCdecl)])]
    public static int CreateEditor(char* directory, int length, nint* view)
    {
        if (directory is null || length is <= 0 or > 32767 || view is null) return -1;
        *view = 0;
        string root = new(directory, 0, length);
        try
        {
            *view = WinRT.MarshalInspectable<FrameworkElement>.FromManaged(TodoEditor.Create(root));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "activation-error.txt"), error.ToString());
            return error.HResult;
        }
    }
}

/// <summary>
/// Editing + persistence slice. Items live in todo-items.json inside the package
/// directory (package-owned data, distinct from host data); recreating the editor
/// reloads the file, so a destroy/recreate round trip is observable.
/// </summary>
internal static class TodoEditor
{
    private static readonly string DataFile = "todo-items.json";

    public static FrameworkElement Create(string root)
    {
        FrameworkElement content = (FrameworkElement)XamlReader.Load(
            File.ReadAllText(Path.Combine(root, "todo.xaml")));
        var input = content.FindName("InputBox").As<TextBox>();
        var addButton = content.FindName("AddButton").As<Button>();
        var list = content.FindName("ItemsList").As<ListView>();
        var status = content.FindName("StatusText").As<TextBlock>();

        List<string> items = Load(root);
        foreach (string item in items) list.Items.Add(item);
        status.Text = items.Count == 0 ? "no stored items" : $"{items.Count} stored item(s) reloaded";

        addButton.Click += (_, _) =>
        {
            string text = input.Text.Trim();
            if (text.Length == 0)
            {
                status.Text = "empty input ignored";
                return;
            }
            items.Add(text);
            list.Items.Add(text);
            input.Text = string.Empty;
            Save(root, items);
            status.Text = $"saved {items.Count} item(s)";
        };
        return content;
    }

    private static List<string> Load(string root)
    {
        string path = Path.Combine(root, DataFile);
        if (!File.Exists(path)) return [];
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        List<string> items = [];
        foreach (JsonElement element in document.RootElement.GetProperty("items").EnumerateArray())
        {
            items.Add(element.GetString() ?? "");
        }
        return items;
    }

    private static void Save(string root, List<string> items)
    {
        using var stream = File.Create(Path.Combine(root, DataFile));
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteStartArray("items");
        foreach (string item in items) writer.WriteStringValue(item);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
