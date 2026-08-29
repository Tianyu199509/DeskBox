using System.Text.Json;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi;

/// <summary>Declares the thread a handler must execute on.</summary>
public enum CommandThreadAffinity
{
    /// <summary>Handler touches only headless services and may run on the pipe thread.</summary>
    Any,

    /// <summary>
    /// Handler must marshal to the WinUI DispatcherQueue before touching UI
    /// state. Handlers running here must stay short: an AI client polling in
    /// a loop must never be able to stall the UI thread.
    /// </summary>
    UiThread,
}

/// <summary>Static registration metadata for one command API method.</summary>
public sealed record CommandRegistration(
    string Method,
    CommandThreadAffinity ThreadAffinity,
    string Capability,
    bool MutatesState,
    bool Destructive,
    string Summary,
    IReadOnlyList<CommandArgumentDescriptor> Arguments,
    string? ExampleRequestJson,
    string? ExampleResponseJson)
{
    /// <summary>Derives the schema-facing category from the method name ("todo/add" → "todo").</summary>
    public string Category => Method.Split('/')[0];

    public CommandDescriptor ToDescriptor() => new(
        Method,
        Category,
        Capability,
        MutatesState,
        Destructive,
        ThreadAffinity == CommandThreadAffinity.UiThread ? "ui-thread" : "any",
        Summary,
        Arguments,
        ExampleRequestJson,
        ExampleResponseJson);
}

/// <summary>Per-execution context handed to handlers.</summary>
public readonly record struct CommandExecutionContext(
    IServiceProvider Services,
    bool DryRun,
    string? IdempotencyKey,
    CancellationToken CancellationToken);

/// <summary>One command API method implementation.</summary>
public interface ICommandHandler
{
    CommandRegistration Registration { get; }

    Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken);
}

/// <summary>Marshals work onto the app UI thread (implemented by App over DispatcherQueue).</summary>
public interface ICommandUiDispatcher
{
    /// <summary>Queues the work; returns false if the queue rejected it (app shutting down).</summary>
    bool TryPost(Action work);
}

/// <summary>
/// Shared argument-reading helpers for handlers. Handlers never throw on
/// malformed arguments: they return stable
/// <see cref="CommandApiProtocol.ErrorCodes.ValidationFailed"/> errors with a
/// hint so AI clients can self-correct.
/// </summary>
public static class CommandArguments
{
    public static CommandErrorPayload ValidationFailed(string message, string hint)
        => new()
        {
            Code = CommandApiProtocol.ErrorCodes.ValidationFailed,
            Phase = "validate",
            Message = message,
            Hint = hint,
        };

    public static bool TryGetString(JsonElement arguments, string name, out string value)
    {
        value = string.Empty;
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    public static bool TryGetInt(JsonElement arguments, string name, out int value)
    {
        value = 0;
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out JsonElement property)
            || property.ValueKind is not (JsonValueKind.Number))
        {
            return false;
        }

        return property.TryGetInt32(out value);
    }

    public static bool TryGetBool(JsonElement arguments, string name, out bool value)
    {
        value = false;
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    /// <summary>Reads an optional array of non-empty strings; missing property yields an empty list.</summary>
    public static bool TryGetStringArray(JsonElement arguments, string name, out List<string> values)
    {
        values = [];
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out JsonElement property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                values.Add(item.GetString()!);
            }
        }

        return true;
    }
}
