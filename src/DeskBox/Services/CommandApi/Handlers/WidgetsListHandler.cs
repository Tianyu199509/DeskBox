using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

public sealed record WidgetListItem(
    string Id,
    string Kind,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    bool Visible,
    bool Disabled,
    string? MappedFolderPath);

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
/// Enumerates every configured widget — including ones whose window is not
/// currently loaded — by widget id, kind, name, rectangle, and mapped path.
/// The id reported here is the handle all other widget commands require.
/// Runs on the UI thread because the settings widget collection is mutated
/// there.
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
        Summary: "Lists every configured widget with id, kind, name, rectangle, visibility, and mapped folder path.",
        Arguments: [],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":8,"method":"widgets/list","params":{"protocolVersion":1,"clientName":"deskbox-cli"}}""",
        ExampleResponseJson: """{"result":{"data":{"count":1,"widgets":[{"id":"3f2a","kind":"Todo","name":"Todo","x":100,"y":80,"width":280,"height":400,"visible":true}]}}}""");

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

        IReadOnlyList<WidgetConfig> configs = widgetManager.GetWidgetConfigSnapshot();
        List<WidgetListItem> widgets = new(configs.Count);
        foreach (WidgetConfig config in configs)
        {
            widgets.Add(new WidgetListItem(
                Id: config.Id,
                Kind: config.WidgetKind.ToString(),
                Name: config.Name,
                X: (int)config.X,
                Y: (int)config.Y,
                Width: (int)config.Width,
                Height: (int)config.Height,
                Visible: config.IsVisible,
                Disabled: config.IsDisabled,
                MappedFolderPath: config.MappedFolderPath));
        }

        WidgetListResult result = new(widgets.Count, widgets);
        return Task.FromResult(JsonSerializer.SerializeToElement(result, WidgetsListJsonContext.Default.WidgetListResult));
    }
}
