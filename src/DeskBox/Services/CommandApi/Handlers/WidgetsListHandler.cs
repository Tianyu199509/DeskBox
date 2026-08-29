using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Helpers;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

public sealed record WidgetListItem(
    long Hwnd,
    string Title,
    string ClassName,
    int X,
    int Y,
    int Width,
    int Height,
    bool Visible);

public sealed record WidgetListResult(int Count, IReadOnlyList<WidgetListItem> Widgets);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WidgetListResult), TypeInfoPropertyName = "WidgetListResult")]
[JsonSerializable(typeof(WidgetListItem), TypeInfoPropertyName = "WidgetListItem")]
internal sealed partial class WidgetsListJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Enumerates live widget windows via the <see cref="WidgetManager"/>. Runs
/// on the UI thread (declared UiThread affinity) because widget window
/// state is UI-bound; the dispatcher applies its short UI timeout so an AI
/// polling loop can never hold the UI thread hostage.
/// </summary>
public sealed class WidgetsListHandler : ICommandHandler
{
    private readonly Func<WidgetManager?> _widgetManager;

    public WidgetsListHandler(Func<WidgetManager?> widgetManager)
    {
        _widgetManager = widgetManager;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "widgets/list",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.LayoutRead,
        MutatesState: false,
        Destructive: false,
        Summary: "Lists live widget windows with hwnd, title, class name, and on-screen rectangle.",
        Arguments: [],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":8,"method":"widgets/list","params":{"protocolVersion":1,"clientName":"deskbox-cli"}}""",
        ExampleResponseJson: """{"result":{"data":{"count":1,"widgets":[{"hwnd":123,"title":"Todo","className":"DeskBox_Widget"}]}}}""");

    public Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        WidgetManager? widgetManager = _widgetManager();
        if (widgetManager is null)
        {
            WidgetListResult notReady = new(0, []);
            return Task.FromResult(JsonSerializer.SerializeToElement(notReady, WidgetsListJsonContext.Default.WidgetListResult));
        }

        IReadOnlyList<IntPtr> handles = widgetManager.GetAllWidgetWindowHandles();
        List<WidgetListItem> widgets = new(handles.Count);
        foreach (IntPtr hwnd in handles)
        {
            Win32Helper.RECT rect = default;
            bool hasRect = Win32Helper.GetWindowRect(hwnd, out rect);
            widgets.Add(new WidgetListItem(
                Hwnd: hwnd.ToInt64(),
                Title: Win32Helper.GetWindowTitle(hwnd),
                ClassName: Win32Helper.GetWindowClassName(hwnd),
                X: hasRect ? rect.Left : 0,
                Y: hasRect ? rect.Top : 0,
                Width: hasRect ? Math.Max(0, rect.Right - rect.Left) : 0,
                Height: hasRect ? Math.Max(0, rect.Bottom - rect.Top) : 0,
                Visible: Win32Helper.IsWindowVisible(hwnd)));
        }

        WidgetListResult result = new(widgets.Count, widgets);
        return Task.FromResult(JsonSerializer.SerializeToElement(result, WidgetsListJsonContext.Default.WidgetListResult));
    }
}
