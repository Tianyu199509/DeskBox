using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

public sealed record GroupMutationResult(string Action, bool Ok, string SourceWidgetId, string TargetWidgetId);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GroupMutationResult), TypeInfoPropertyName = "GroupMutationResult")]
internal sealed partial class GroupJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Widget group commands. The WidgetManager group methods marshal
/// themselves onto the UI thread when called from elsewhere, so these
/// handlers run headless on the pipe thread and simply surface the bool
/// outcome (false = the group operation was rejected, e.g. overlapping
/// windows that cannot merge).
/// </summary>
public sealed class GroupsMergeHandler : ICommandHandler
{
    private readonly Func<WidgetManager?> _widgetManager;

    public GroupsMergeHandler(Func<WidgetManager?> widgetManager)
    {
        _widgetManager = widgetManager;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "groups/merge",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.WidgetsWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Merges one widget into another, forming (or joining) a widget group.",
        Arguments:
        [
            new CommandArgumentDescriptor("sourceWidgetId", "string", true, "Widget to merge.", "\"a1\""),
            new CommandArgumentDescriptor("targetWidgetId", "string", true, "Widget to merge into (group anchor).", "\"b2\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":26,"method":"groups/merge","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"sourceWidgetId":"a1","targetWidgetId":"b2"}}}""",
        ExampleResponseJson: """{"result":{"data":{"action":"merge","ok":true,"sourceWidgetId":"a1","targetWidgetId":"b2"}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        WidgetManager? widgetManager = _widgetManager()
            ?? throw WidgetLifecycle.NotLoaded("widget-manager", "DeskBox is still starting; retry shortly.");
        if (!CommandArguments.TryGetString(arguments, "sourceWidgetId", out string source)
            || !CommandArguments.TryGetString(arguments, "targetWidgetId", out string target)
            || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'sourceWidgetId' and 'targetWidgetId' arguments are required.",
                "Call widgets/list first; source joins target's group.");
        }

        bool ok = await widgetManager.MergeWidgetsAsync(source, target).ConfigureAwait(false);
        GroupMutationResult result = new("merge", ok, source, target);
        return JsonSerializer.SerializeToElement(result, GroupJsonContext.Default.GroupMutationResult);
    }
}

/// <summary>Dissolves the group that contains the given widget; members
/// return to standalone windows.</summary>
public sealed class GroupsDissolveHandler : ICommandHandler
{
    private readonly Func<WidgetManager?> _widgetManager;

    public GroupsDissolveHandler(Func<WidgetManager?> widgetManager)
    {
        _widgetManager = widgetManager;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "groups/dissolve",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.WidgetsWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Dissolves the widget group containing the given widget; members become standalone.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Any widget inside the group.", "\"b2\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":27,"method":"groups/dissolve","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"b2"}}}""",
        ExampleResponseJson: """{"result":{"data":{"action":"dissolve","ok":true,"sourceWidgetId":"b2","targetWidgetId":""}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        WidgetManager? widgetManager = _widgetManager()
            ?? throw WidgetLifecycle.NotLoaded("widget-manager", "DeskBox is still starting; retry shortly.");
        string widgetId = CommandArguments.RequireWidgetId(arguments);

        bool ok = await widgetManager.DissolveWidgetGroupContainingAsync(widgetId).ConfigureAwait(false);
        GroupMutationResult result = new("dissolve", ok, widgetId, string.Empty);
        return JsonSerializer.SerializeToElement(result, GroupJsonContext.Default.GroupMutationResult);
    }
}
