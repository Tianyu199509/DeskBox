using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

public sealed record TodoItemSummary(
    string Id,
    string Text,
    bool IsCompleted,
    bool IsImportant,
    string? ColorMarker,
    DateTimeOffset? DueDate,
    string? Notes);

public sealed record TodoListResult(string WidgetId, int TotalCount, IReadOnlyList<TodoItemSummary> Items);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TodoListResult), TypeInfoPropertyName = "TodoListResult")]
[JsonSerializable(typeof(TodoItemSummary), TypeInfoPropertyName = "TodoItemSummary")]
internal sealed partial class TodoHandlersJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Reads a todo widget store directly (same <see cref="TodoWidgetStore"/>
/// path and <see cref="ResilientJsonStore"/> semantics the widget UI uses).
/// Todo data is per-widget, so callers must pass the widget id reported by
/// <c>widgets/list</c>.
/// </summary>
public sealed class TodoListHandler : ICommandHandler
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public CommandRegistration Registration { get; } = new(
        Method: "todo/list",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.TodoRead,
        MutatesState: false,
        Destructive: false,
        Summary: "Lists the items of one todo widget, ordered incomplete-first then by list order.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true,
                "Todo widget id (from widgets/list).", "\"3f2a\""),
            new CommandArgumentDescriptor("limit", "integer", false,
                "Maximum number of items to return (1-500, default 100).", "100"),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":5,"method":"todo/list","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"3f2a"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"3f2a","totalCount":2,"items":[{"id":"x","text":"买牛奶","isCompleted":false}]}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = RequireWidgetId(arguments);
        int limit = DefaultLimit;
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("limit", out JsonElement limitProperty))
        {
            if (limitProperty.ValueKind != JsonValueKind.Number
                || !limitProperty.TryGetInt32(out limit)
                || limit < 1
                || limit > MaxLimit)
            {
                throw CommandValidationException.ValidationFailed(
                    $"The 'limit' argument must be an integer between 1 and {MaxLimit}.",
                    $"Retry with {{\"limit\":{DefaultLimit}}} or omit the argument.");
            }
        }

        TodoWidgetStore store = new(widgetId);
        TodoWidgetData data = await store.LoadAsync().ConfigureAwait(false);
        List<TodoItemSummary> items = data.Items
            .OrderBy(item => item.IsCompleted)
            .Take(limit)
            .Select(ToSummary)
            .ToList();
        TodoListResult result = new(widgetId, data.Items.Count, items);
        return JsonSerializer.SerializeToElement(result, TodoHandlersJsonContext.Default.TodoListResult);
    }

    private static TodoItemSummary ToSummary(TodoItem item)
        => new(
            item.Id,
            item.Text,
            item.IsCompleted,
            item.IsImportant,
            item.ColorMarker,
            item.DueDate,
            item.Notes);

    internal static string RequireWidgetId(JsonElement arguments)
    {
        if (!CommandArguments.TryGetString(arguments, "widgetId", out string widgetId)
            || string.IsNullOrWhiteSpace(widgetId))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'widgetId' argument is required.",
                "Call widgets/list first and pass the id of a todo widget.");
        }

        return widgetId;
    }
}

public sealed record TodoAddResult(string WidgetId, string ItemId, int ItemCount, bool Saved);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TodoAddResult), TypeInfoPropertyName = "TodoAddResult")]
internal sealed partial class TodoAddJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Appends one todo item to a widget store. The running widget UI reloads
/// from the store on change notifications; items written here appear in the
/// widget on its next load. Honors DryRun.
/// </summary>
public sealed class TodoAddHandler : ICommandHandler
{
    public CommandRegistration Registration { get; } = new(
        Method: "todo/add",
        ThreadAffinity: CommandThreadAffinity.Any,
        Capability: CommandApiProtocol.Capabilities.TodoWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Adds one todo item to a todo widget store.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true,
                "Todo widget id (from widgets/list).", "\"3f2a\""),
            new CommandArgumentDescriptor("text", "string", true, "Item text.", "\"买牛奶\""),
            new CommandArgumentDescriptor("important", "boolean", false, "Mark as important (default false).", "true"),
            new CommandArgumentDescriptor("colorMarker", "string", false,
                $"One of: {string.Join(", ", TodoItem.SupportedColorMarkers)}.", "\"red\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":6,"method":"todo/add","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"3f2a","text":"买牛奶"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"3f2a","itemId":"abc","itemCount":3,"saved":true}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = TodoListHandler.RequireWidgetId(arguments);
        if (!CommandArguments.TryGetString(arguments, "text", out string text) || string.IsNullOrWhiteSpace(text))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'text' argument is required and must be non-empty.",
                """Retry with {"widgetId":"<id>","text":"<todo text>"}.""");
        }

        string? colorMarker = null;
        if (CommandArguments.TryGetString(arguments, "colorMarker", out string markerValue)
            && !string.IsNullOrWhiteSpace(markerValue))
        {
            if (!TodoItem.SupportedColorMarkers.Contains(markerValue, StringComparer.Ordinal))
            {
                throw CommandValidationException.ValidationFailed(
                    $"Unknown colorMarker '{markerValue}'.",
                    $"Use one of: {string.Join(", ", TodoItem.SupportedColorMarkers)}.");
            }

            colorMarker = markerValue;
        }

        bool important = CommandArguments.TryGetBool(arguments, "important", out bool importantValue) && importantValue;

        TodoWidgetStore store = new(widgetId);
        TodoWidgetData data = await store.LoadAsync().ConfigureAwait(false);
        int existingCount = data.Items.Count;
        if (context.DryRun)
        {
            TodoAddResult dryRunResult = new(widgetId, "dry-run", existingCount + 1, Saved: false);
            return JsonSerializer.SerializeToElement(dryRunResult, TodoAddJsonContext.Default.TodoAddResult);
        }

        TodoItem item = new()
        {
            Text = text,
            IsImportant = important,
            ColorMarker = colorMarker,
        };
        data.Items.Add(item);
        await store.SaveAsync(data).ConfigureAwait(false);
        TodoAddResult result = new(widgetId, item.Id, data.Items.Count, Saved: true);
        return JsonSerializer.SerializeToElement(result, TodoAddJsonContext.Default.TodoAddResult);
    }
}
