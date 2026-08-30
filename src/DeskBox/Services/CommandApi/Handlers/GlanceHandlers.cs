using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Protocol;
using DeskBox.ViewModels;

namespace DeskBox.Services.CommandApi.Handlers;

/// <summary>
/// Glance photo-widget commands. Data reads go headless through
/// GlanceWidgetStore (its Changed event refreshes the open widget);
/// flip/pause act on the live view model and need the UI thread.
/// </summary>
public sealed record GlanceInfoResult(
    string WidgetId,
    string Layout,
    string Transition,
    int LocalImageCount,
    int RotationIntervalMinutes,
    bool RandomOrder);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GlanceInfoResult), TypeInfoPropertyName = "GlanceInfoResult")]
internal sealed partial class GlanceJsonContext : JsonSerializerContext
{
}

/// <summary>Reads one glance widget's persisted configuration headlessly
/// (layout, transition, image count, rotation settings).</summary>
public sealed class GlanceGetHandler : ICommandHandler
{
    public CommandRegistration Registration { get; } = new(
        Method: "glance/get",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.GlanceRead,
        MutatesState: false,
        Destructive: false,
        Summary: "Reads one glance widget's persisted settings (layout, transition, local image count, rotation).",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Glance widget id (from widgets/list).", "\"g1\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":37,"method":"glance/get","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"g1"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"g1","layout":"Editorial","transition":"SlideFade","localImageCount":4,"rotationIntervalMinutes":10,"randomOrder":false}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = CommandArguments.RequireWidgetId(arguments);
        GlanceWidgetStore store = GlanceWidgetStore.ForWidget(widgetId);
        GlanceWidgetData data = await store.LoadAsync().ConfigureAwait(false);
        GlanceInfoResult result = new(
            widgetId,
            data.Layout.ToString(),
            data.Transition.ToString(),
            data.LocalImagePaths?.Count ?? 0,
            data.RotationIntervalMinutes,
            data.RandomOrder);
        return JsonSerializer.SerializeToElement(result, GlanceJsonContext.Default.GlanceInfoResult);
    }
}

public sealed record GlanceActionResult(string WidgetId, string Action, bool Ok);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GlanceActionResult), TypeInfoPropertyName = "GlanceActionResult")]
internal sealed partial class GlanceActionJsonContext : JsonSerializerContext
{
}

/// <summary>Advances the glance widget to its next image (triggers online
/// refresh when no local images exist).</summary>
public sealed class GlanceNextHandler : ICommandHandler
{
    private readonly Func<string, GlanceWidgetViewModel?> _resolver;

    public GlanceNextHandler(Func<string, GlanceWidgetViewModel?> resolver)
    {
        _resolver = resolver;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "glance/next",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.GlanceWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Advances the glance widget to its next image.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Glance widget id.", "\"g1\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":38,"method":"glance/next","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"g1"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"g1","action":"next","ok":true}}}""");

    public Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = CommandArguments.RequireWidgetId(arguments);
        GlanceWidgetViewModel? viewModel = _resolver(widgetId);
        if (viewModel is null)
        {
            throw WidgetLifecycle.NotLoaded(widgetId, "Call widgets/show with this widgetId first, then retry.");
        }

        // NextImage() advances the carousel; the view model raises change
        // notifications the open widget renders directly.
        viewModel.NextImage();
        GlanceActionResult result = new(widgetId, "next", true);
        return Task.FromResult(JsonSerializer.SerializeToElement(result, GlanceActionJsonContext.Default.GlanceActionResult));
    }
}

/// <summary>Pauses or resumes the glance widget's auto-rotation.</summary>
public sealed class GlanceTogglePauseHandler : ICommandHandler
{
    private readonly Func<string, GlanceWidgetViewModel?> _resolver;

    public GlanceTogglePauseHandler(Func<string, GlanceWidgetViewModel?> resolver)
    {
        _resolver = resolver;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "glance/toggle-pause",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.GlanceWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Toggles pause/resume of the glance widget's auto-rotation.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Glance widget id.", "\"g1\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":39,"method":"glance/toggle-pause","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"g1"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"g1","action":"toggle-pause","ok":true}}}""");

    public Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = CommandArguments.RequireWidgetId(arguments);
        GlanceWidgetViewModel? viewModel = _resolver(widgetId);
        if (viewModel is null)
        {
            throw WidgetLifecycle.NotLoaded(widgetId, "Call widgets/show with this widgetId first, then retry.");
        }

        viewModel.TogglePause();
        GlanceActionResult result = new(widgetId, "toggle-pause", true);
        return Task.FromResult(JsonSerializer.SerializeToElement(result, GlanceActionJsonContext.Default.GlanceActionResult));
    }
}
