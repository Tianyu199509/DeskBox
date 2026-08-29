using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

public sealed record QuickCaptureItemSummary(
    string Id,
    string Type,
    string Body,
    string? Title,
    string? Url,
    string? ImagePath,
    bool IsPinned,
    bool IsRecent,
    IReadOnlyList<string> Tags,
    int SortOrder);

public sealed record QuickCaptureListResult(int TotalCount, IReadOnlyList<QuickCaptureItemSummary> Items);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(QuickCaptureListResult), TypeInfoPropertyName = "QcListResult")]
[JsonSerializable(typeof(QuickCaptureItemSummary), TypeInfoPropertyName = "QcItemSummary")]
internal sealed partial class QuickCaptureHandlersJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Reads the quick capture store through the live <see cref="QuickCaptureService"/>,
/// so results always match what the widget shows. The service serializes
/// loads through a semaphore, so calling from the pipe thread is safe.
/// </summary>
public sealed class QuickCaptureListHandler : ICommandHandler
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly Func<QuickCaptureService> _quickCaptureService;

    public QuickCaptureListHandler(Func<QuickCaptureService> quickCaptureService)
    {
        _quickCaptureService = quickCaptureService;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "quickcapture/list",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.QuickCaptureRead,
        MutatesState: false,
        Destructive: false,
        Summary: "Lists quick capture items (newest and pinned first), oldest last, up to the requested limit.",
        Arguments:
        [
            new CommandArgumentDescriptor("limit", "integer", false,
                "Maximum number of items to return (1-200, default 50).", "50"),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":3,"method":"quickcapture/list","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"limit":20}}}""",
        ExampleResponseJson: """{"result":{"data":{"totalCount":1,"items":[{"id":"abc","type":"Text","body":"hello"}]}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        int limit = DefaultLimit;
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("limit", out JsonElement limitProperty))
        {
            if (limitProperty.ValueKind != JsonValueKind.Number
                || !limitProperty.TryGetInt32(out limit)
                || limit < 1
                || limit > MaxLimit)
            {
                throw new CommandValidationException(CommandArguments.ValidationFailed(
                    $"The 'limit' argument must be an integer between 1 and {MaxLimit}.",
                    $"Retry with {{\"limit\":{DefaultLimit}}} or omit the argument."));
            }
        }

        QuickCaptureStoreData data = await _quickCaptureService().GetDataAsync().ConfigureAwait(false);
        List<QuickCaptureItemSummary> items = data.Items
            .Where(item => !item.IsDeleted)
            .OrderByDescending(item => item.IsPinned)
            .ThenBy(item => item.SortOrder)
            .Take(limit)
            .Select(ToSummary)
            .ToList();
        QuickCaptureListResult result = new(data.Items.Count(item => !item.IsDeleted), items);
        return JsonSerializer.SerializeToElement(result, QuickCaptureHandlersJsonContext.Default.QcListResult);
    }

    private static QuickCaptureItemSummary ToSummary(QuickCaptureItem item)
        => new(
            item.Id,
            item.Type.ToString(),
            item.Body,
            item.Title,
            item.Url,
            item.ImagePath,
            item.IsPinned,
            item.IsRecent,
            item.Tags,
            item.SortOrder);
}

public sealed record QuickCaptureAddResult(string Id, bool Saved, bool WasTruncated);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(QuickCaptureAddResult), TypeInfoPropertyName = "QcAddResult")]
internal sealed partial class QuickCaptureAddJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Appends a text item through the live service. Honors DryRun (validates
/// only) and the item body limit enforced by the service.
/// </summary>
public sealed class QuickCaptureAddHandler : ICommandHandler
{
    private readonly Func<QuickCaptureService> _quickCaptureService;

    public QuickCaptureAddHandler(Func<QuickCaptureService> quickCaptureService)
    {
        _quickCaptureService = quickCaptureService;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "quickcapture/add",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.QuickCaptureWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Adds a text note to the quick capture widget (the same store the widget UI writes).",
        Arguments:
        [
            new CommandArgumentDescriptor("body", "string", true,
                $"Note text (max {QuickCaptureService.MaxItemBodyCharacters} characters).", "\"记得买牛奶\""),
            new CommandArgumentDescriptor("title", "string", false, "Optional title.", "\"购物\""),
            new CommandArgumentDescriptor("pin", "boolean", false, "Pin the item (default false).", "true"),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":4,"method":"quickcapture/add","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"body":"记得买牛奶","pin":false}}}""",
        ExampleResponseJson: """{"result":{"data":{"id":"abc","saved":true,"wasTruncated":false}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!CommandArguments.TryGetString(arguments, "body", out string body) || string.IsNullOrWhiteSpace(body))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'body' argument is required and must be non-empty text.",
                """Retry with {"body":"<note text>"}.""");
        }

        if (body.Length > QuickCaptureService.MaxItemBodyCharacters)
        {
            throw CommandValidationException.ValidationFailed(
                $"The 'body' argument exceeds the {QuickCaptureService.MaxItemBodyCharacters} character limit ({body.Length} given).",
                "Split the note into smaller items.");
        }

        CommandArguments.TryGetString(arguments, "title", out string? title);
        bool pin = CommandArguments.TryGetBool(arguments, "pin", out bool pinValue) && pinValue;

        if (context.DryRun)
        {
            QuickCaptureAddResult dryRunResult = new(Id: "dry-run", Saved: false, WasTruncated: false);
            return JsonSerializer.SerializeToElement(dryRunResult, QuickCaptureAddJsonContext.Default.QcAddResult);
        }

        QuickCaptureService service = _quickCaptureService();
        QuickCaptureWriteResult writeResult = await service
            .AddDetailedItemWithResultAsync(
                string.IsNullOrWhiteSpace(title) ? null : title,
                body,
                QuickCaptureAppearancePreset.Default,
                TextContentFormat.PlainText,
                pin)
            .ConfigureAwait(false);
        QuickCaptureAddResult result = new(
            writeResult.Item?.Id ?? string.Empty,
            writeResult.Saved,
            writeResult.WasTruncated);
        return JsonSerializer.SerializeToElement(result, QuickCaptureAddJsonContext.Default.QcAddResult);
    }
}
