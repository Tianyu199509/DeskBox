using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Protocol;
using DeskBox.ViewModels;

namespace DeskBox.Services.CommandApi.Handlers;

/// <summary>
/// Mutating todo commands. All run on the UI thread through the live
/// TodoWidgetViewModel: this keeps recurrence generation, undo history, and
/// the open widget's UI in sync — writing the store directly would leave
/// the visible widget stale until reload.
/// </summary>
internal static class TodoMutations
{
    public static TodoWidgetViewModel RequireViewModel(
        Func<string, TodoWidgetViewModel?> resolver,
        string widgetId)
    {
        TodoWidgetViewModel? viewModel = resolver(widgetId);
        if (viewModel is null)
        {
            throw new CommandValidationException(new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.WidgetNotLoaded,
                Phase = "execute",
                Message = $"Todo widget '{widgetId}' is configured but not currently loaded.",
                Hint = "Call widgets/show with this widgetId first, then retry.",
            });
        }

        return viewModel;
    }

    public static string RequireItemId(JsonElement arguments)
    {
        if (!CommandArguments.TryGetString(arguments, "itemId", out string itemId)
            || string.IsNullOrWhiteSpace(itemId))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'itemId' argument is required.",
                "Call todo/list first and pass the id of the target item.");
        }

        return itemId;
    }
}

public sealed record TodoMutationResult(string WidgetId, bool Found, string Action, int AffectedCount);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TodoMutationResult), TypeInfoPropertyName = "TodoMutationResult")]
internal sealed partial class TodoMutationJsonContext : JsonSerializerContext
{
}

/// <summary>Marks one todo item completed or not. Completing a recurring
/// item generates the next occurrence through the view model.</summary>
public sealed class TodoSetCompletedHandler : ICommandHandler
{
    private readonly Func<string, TodoWidgetViewModel?> _resolver;

    public TodoSetCompletedHandler(Func<string, TodoWidgetViewModel?> resolver)
    {
        _resolver = resolver;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "todo/set-completed",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.TodoWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Marks one todo item completed or not completed (recurring items generate their next occurrence).",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Todo widget id.", "\"3f2a\""),
            new CommandArgumentDescriptor("itemId", "string", true, "Item id (from todo/list).", "\"x1\""),
            new CommandArgumentDescriptor("isCompleted", "boolean", true, "Target completion state.", "true"),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":15,"method":"todo/set-completed","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"3f2a","itemId":"x1","isCompleted":true}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"3f2a","found":true,"action":"set-completed","affectedCount":1}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = TodoListHandler.RequireWidgetId(arguments);
        TodoWidgetViewModel viewModel = TodoMutations.RequireViewModel(_resolver, widgetId);
        string itemId = TodoMutations.RequireItemId(arguments);
        if (!CommandArguments.TryGetBool(arguments, "isCompleted", out bool isCompleted))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'isCompleted' argument is required (true or false).",
                """Retry with {"isCompleted":true}.""");
        }

        bool found = await viewModel.SetCompletedAsync(itemId, isCompleted).ConfigureAwait(true);
        TodoMutationResult result = new(widgetId, found, "set-completed", found ? 1 : 0);
        return JsonSerializer.SerializeToElement(result, TodoMutationJsonContext.Default.TodoMutationResult);
    }
}

/// <summary>Deletes one or more todo items by id (undoable inside the
/// widget's own undo stack, not across CLI sessions).</summary>
public sealed class TodoDeleteHandler : ICommandHandler
{
    private readonly Func<string, TodoWidgetViewModel?> _resolver;

    public TodoDeleteHandler(Func<string, TodoWidgetViewModel?> resolver)
    {
        _resolver = resolver;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "todo/delete",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.TodoWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Deletes one or more todo items by id.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Todo widget id.", "\"3f2a\""),
            new CommandArgumentDescriptor("itemIds", "array", true, "Item ids to delete.", "[\"x1\",\"x2\"]"),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":16,"method":"todo/delete","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"3f2a","itemIds":["x1"]}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"3f2a","found":true,"action":"delete","affectedCount":2}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = TodoListHandler.RequireWidgetId(arguments);
        TodoWidgetViewModel viewModel = TodoMutations.RequireViewModel(_resolver, widgetId);
        if (!CommandArguments.TryGetStringArray(arguments, "itemIds", out List<string> itemIds)
            || itemIds.Count == 0)
        {
            throw CommandValidationException.ValidationFailed(
                "The 'itemIds' argument is required and must be a non-empty array of item ids.",
                """Retry with {"itemIds":["<id1>"]}. Ask for ids via todo/list.""");
        }

        int deleted = await viewModel.DeleteItemsAsync(itemIds).ConfigureAwait(true);
        TodoMutationResult result = new(widgetId, deleted > 0, "delete", deleted);
        return JsonSerializer.SerializeToElement(result, TodoMutationJsonContext.Default.TodoMutationResult);
    }
}

/// <summary>Renames one todo item.</summary>
public sealed class TodoEditHandler : ICommandHandler
{
    private readonly Func<string, TodoWidgetViewModel?> _resolver;

    public TodoEditHandler(Func<string, TodoWidgetViewModel?> resolver)
    {
        _resolver = resolver;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "todo/edit",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.TodoWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Replaces the text of one todo item.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Todo widget id.", "\"3f2a\""),
            new CommandArgumentDescriptor("itemId", "string", true, "Item id.", "\"x1\""),
            new CommandArgumentDescriptor("text", "string", true, "New text.", "\"买牛奶（改成两盒）\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":17,"method":"todo/edit","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"3f2a","itemId":"x1","text":"买牛奶"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"3f2a","found":true,"action":"edit","affectedCount":1}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = TodoListHandler.RequireWidgetId(arguments);
        TodoWidgetViewModel viewModel = TodoMutations.RequireViewModel(_resolver, widgetId);
        string itemId = TodoMutations.RequireItemId(arguments);
        if (!CommandArguments.TryGetString(arguments, "text", out string text)
            || string.IsNullOrWhiteSpace(text))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'text' argument is required and must be non-empty.",
                """Retry with {"text":"<new text>"}.""");
        }

        bool found = await viewModel.UpdateItemTextAsync(itemId, text).ConfigureAwait(true);
        TodoMutationResult result = new(widgetId, found, "edit", found ? 1 : 0);
        return JsonSerializer.SerializeToElement(result, TodoMutationJsonContext.Default.TodoMutationResult);
    }
}

/// <summary>Sets or clears the due date of one todo item (ISO 8601; null clears).</summary>
public sealed class TodoSetDueDateHandler : ICommandHandler
{
    private readonly Func<string, TodoWidgetViewModel?> _resolver;

    public TodoSetDueDateHandler(Func<string, TodoWidgetViewModel?> resolver)
    {
        _resolver = resolver;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "todo/set-due",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.TodoWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Sets or clears the due date of one todo item.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Todo widget id.", "\"3f2a\""),
            new CommandArgumentDescriptor("itemId", "string", true, "Item id.", "\"x1\""),
            new CommandArgumentDescriptor("dueDate", "string", false,
                "ISO 8601 date-time; omit or pass null to clear.", "\"2026-09-01T09:00:00+08:00\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":18,"method":"todo/set-due","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"3f2a","itemId":"x1","dueDate":"2026-09-01T09:00:00+08:00"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"3f2a","found":true,"action":"set-due","affectedCount":1}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = TodoListHandler.RequireWidgetId(arguments);
        TodoWidgetViewModel viewModel = TodoMutations.RequireViewModel(_resolver, widgetId);
        string itemId = TodoMutations.RequireItemId(arguments);

        DateTimeOffset? dueDate = null;
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("dueDate", out JsonElement dueProperty)
            && dueProperty.ValueKind == JsonValueKind.String)
        {
            if (!DateTimeOffset.TryParse(dueProperty.GetString(), out DateTimeOffset parsed))
            {
                throw CommandValidationException.ValidationFailed(
                    "The 'dueDate' argument must be an ISO 8601 date-time string.",
                    """Retry with {"dueDate":"2026-09-01T09:00:00+08:00"} or omit it to clear.""");
            }

            dueDate = parsed;
        }

        bool found = await viewModel.SetDueDateAsync(itemId, dueDate).ConfigureAwait(true);
        TodoMutationResult result = new(widgetId, found, "set-due", found ? 1 : 0);
        return JsonSerializer.SerializeToElement(result, TodoMutationJsonContext.Default.TodoMutationResult);
    }
}

/// <summary>Deletes every completed item in one todo widget.</summary>
public sealed class TodoClearCompletedHandler : ICommandHandler
{
    private readonly Func<string, TodoWidgetViewModel?> _resolver;

    public TodoClearCompletedHandler(Func<string, TodoWidgetViewModel?> resolver)
    {
        _resolver = resolver;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "todo/clear-completed",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.TodoWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Deletes all completed items of one todo widget.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Todo widget id.", "\"3f2a\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":19,"method":"todo/clear-completed","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"3f2a"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"3f2a","found":true,"action":"clear-completed","affectedCount":3}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = TodoListHandler.RequireWidgetId(arguments);
        TodoWidgetViewModel viewModel = TodoMutations.RequireViewModel(_resolver, widgetId);
        int deleted = await viewModel.ClearCompletedAsync().ConfigureAwait(true);
        TodoMutationResult result = new(widgetId, deleted > 0, "clear-completed", deleted);
        return JsonSerializer.SerializeToElement(result, TodoMutationJsonContext.Default.TodoMutationResult);
    }
}
