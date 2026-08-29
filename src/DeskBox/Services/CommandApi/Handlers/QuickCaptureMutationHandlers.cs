using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

/// <summary>
/// Mutating quick capture commands. QuickCaptureService serializes all
/// operations through a semaphore, raises Changed (which refreshes the
/// open widget on the UI thread), and needs no WinUI runtime — so these
/// run headless on the pipe thread.
/// </summary>
public sealed record QuickCaptureMutationResult(
    string Action,
    int RequestedCount,
    int AffectedCount,
    bool Saved);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(QuickCaptureMutationResult), TypeInfoPropertyName = "QcMutationResult")]
internal sealed partial class QuickCaptureMutationJsonContext : JsonSerializerContext
{
}

/// <summary>Pins or unpins one quick capture item.</summary>
public sealed class QuickCapturePinHandler : ICommandHandler
{
    private readonly Func<QuickCaptureService> _service;

    public QuickCapturePinHandler(Func<QuickCaptureService> service)
    {
        _service = service;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "quickcapture/pin",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.QuickCaptureWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Pins or unpins one quick capture item.",
        Arguments:
        [
            new CommandArgumentDescriptor("itemId", "string", true, "Item id (from quickcapture/list).", "\"abc\""),
            new CommandArgumentDescriptor("pinned", "boolean", true, "Target pinned state.", "true"),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":20,"method":"quickcapture/pin","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"itemId":"abc","pinned":true}}}""",
        ExampleResponseJson: """{"result":{"data":{"action":"pin","requestedCount":1,"affectedCount":1,"saved":true}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!CommandArguments.TryGetString(arguments, "itemId", out string itemId)
            || string.IsNullOrWhiteSpace(itemId))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'itemId' argument is required.",
                "Call quickcapture/list first and pass the id of the target item.");
        }

        if (!CommandArguments.TryGetBool(arguments, "pinned", out bool pinned))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'pinned' argument is required (true or false).",
                """Retry with {"pinned":true}.""");
        }

        bool saved = await _service().SetPinnedAsync(itemId, pinned).ConfigureAwait(false);
        QuickCaptureMutationResult result = new("pin", 1, saved ? 1 : 0, saved);
        return JsonSerializer.SerializeToElement(result, QuickCaptureMutationJsonContext.Default.QcMutationResult);
    }
}

/// <summary>Updates the body text of one quick capture item.</summary>
public sealed class QuickCaptureUpdateHandler : ICommandHandler
{
    private readonly Func<QuickCaptureService> _service;

    public QuickCaptureUpdateHandler(Func<QuickCaptureService> service)
    {
        _service = service;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "quickcapture/update",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.QuickCaptureWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Replaces the body text of one quick capture item.",
        Arguments:
        [
            new CommandArgumentDescriptor("itemId", "string", true, "Item id.", "\"abc\""),
            new CommandArgumentDescriptor("body", "string", true, "New body text.", "\"更新的内容\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":21,"method":"quickcapture/update","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"itemId":"abc","body":"更新的内容"}}}""",
        ExampleResponseJson: """{"result":{"data":{"action":"update","requestedCount":1,"affectedCount":1,"saved":true}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!CommandArguments.TryGetString(arguments, "itemId", out string itemId)
            || string.IsNullOrWhiteSpace(itemId))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'itemId' argument is required.",
                "Call quickcapture/list first and pass the id of the target item.");
        }

        if (!CommandArguments.TryGetString(arguments, "body", out string body)
            || string.IsNullOrWhiteSpace(body))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'body' argument is required and must be non-empty.",
                """Retry with {"body":"<new text>"}.""");
        }

        bool saved = await _service().UpdateItemAsync(itemId, body).ConfigureAwait(false);
        QuickCaptureMutationResult result = new("update", 1, saved ? 1 : 0, saved);
        return JsonSerializer.SerializeToElement(result, QuickCaptureMutationJsonContext.Default.QcMutationResult);
    }
}

/// <summary>Permanently deletes one or more quick capture items. Unlike the
/// in-widget delete flow there is no snapshot to restore from, so the CLI
/// surfaces this as a hard delete.</summary>
public sealed class QuickCaptureDeleteHandler : ICommandHandler
{
    private readonly Func<QuickCaptureService> _service;

    public QuickCaptureDeleteHandler(Func<QuickCaptureService> service)
    {
        _service = service;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "quickcapture/delete",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.QuickCaptureWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Permanently deletes one or more quick capture items by id (no restore snapshot via the API).",
        Arguments:
        [
            new CommandArgumentDescriptor("itemIds", "array", true, "Item ids to delete.", "[\"abc\"]"),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":22,"method":"quickcapture/delete","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"itemIds":["abc"]}}}""",
        ExampleResponseJson: """{"result":{"data":{"action":"delete","requestedCount":1,"affectedCount":1,"saved":true}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!CommandArguments.TryGetStringArray(arguments, "itemIds", out List<string> itemIds)
            || itemIds.Count == 0)
        {
            throw CommandValidationException.ValidationFailed(
                "The 'itemIds' argument is required and must be a non-empty array of item ids.",
                """Retry with {"itemIds":["<id>"]}. Ask for ids via quickcapture/list.""");
        }

        IReadOnlyList<QuickCaptureDeletedItemSnapshot> deleted =
            await _service().DeleteItemsAsync(itemIds, isRecent: false).ConfigureAwait(false);
        QuickCaptureMutationResult result = new("delete", itemIds.Count, deleted.Count, true);
        return JsonSerializer.SerializeToElement(result, QuickCaptureMutationJsonContext.Default.QcMutationResult);
    }
}
