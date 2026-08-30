using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

/// <summary>
/// Shared plumbing for widget lifecycle commands. Every handler here runs
/// on the UI thread (window creation/removal is UI-bound) and needs the
/// WidgetManager; a missing manager or an unloaded window maps to a stable
/// widget_not_loaded error with a self-correcting hint.
/// </summary>
internal static class WidgetLifecycle
{
    public static CommandValidationException NotLoaded(string widgetId, string hint)
        => new(new CommandErrorPayload
        {
            Code = CommandApiProtocol.ErrorCodes.WidgetNotLoaded,
            Phase = "execute",
            Message = $"Widget '{widgetId}' is configured but its window is not loaded.",
            Hint = hint,
        });

    public static CommandValidationException Validation(string message, string hint)
        => CommandValidationException.ValidationFailed(message, hint);

    public static string RequireWidgetId(JsonElement arguments)
    {
        if (!CommandArguments.TryGetString(arguments, "widgetId", out string widgetId)
            || string.IsNullOrWhiteSpace(widgetId))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'widgetId' argument is required.",
                "Call widgets/list first and pass the id of the target widget.");
        }

        return widgetId;
    }
}

public sealed record WidgetCreatedResult(string WidgetId, string Kind, bool Created);

public sealed record WidgetMutatedResult(string WidgetId, bool Ok, string Action);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WidgetCreatedResult), TypeInfoPropertyName = "WidgetCreatedResult")]
[JsonSerializable(typeof(WidgetMutatedResult), TypeInfoPropertyName = "WidgetMutatedResult")]
internal sealed partial class WidgetLifecycleJsonContext : JsonSerializerContext
{
}

/// <summary>Creates a widget of the given kind. "file" widgets get managed
/// storage; "folder" widgets map an existing path; feature kinds (todo,
/// glance, music, weather, search) create the singleton feature widget.</summary>
public sealed class WidgetsCreateHandler : ICommandHandler
{
    private readonly Func<WidgetManager?> _widgetManager;

    public WidgetsCreateHandler(Func<WidgetManager?> widgetManager)
    {
        _widgetManager = widgetManager;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "widgets/create",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.WidgetsWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Creates a widget. kind: file | folder (with path) | todo | glance | music | weather | search.",
        Arguments:
        [
            new CommandArgumentDescriptor("kind", "string", true,
                "Widget kind to create.", "\"todo\""),
            new CommandArgumentDescriptor("path", "string", false,
                "Folder path to map (required for kind=folder).", "\"D:\\Photos\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":10,"method":"widgets/create","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"kind":"todo"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"abc","kind":"Todo"}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        WidgetManager? widgetManager = _widgetManager()
            ?? throw WidgetLifecycle.NotLoaded("widget-manager", "DeskBox is still starting; retry shortly.");

        if (!CommandArguments.TryGetString(arguments, "kind", out string kind)
            || string.IsNullOrWhiteSpace(kind))
        {
            throw WidgetLifecycle.Validation(
                "The 'kind' argument is required.",
                "Use one of: file, folder, todo, glance, music, weather, search.");
        }

        string before = SerializeWidgetIds(widgetManager);
        bool isFeatureKind = !kind.Equals("folder", StringComparison.OrdinalIgnoreCase)
            && !kind.Equals("file", StringComparison.OrdinalIgnoreCase);
        if (kind.Equals("folder", StringComparison.OrdinalIgnoreCase))
        {
            if (!CommandArguments.TryGetString(arguments, "path", out string folderPath)
                || string.IsNullOrWhiteSpace(folderPath))
            {
                throw WidgetLifecycle.Validation(
                    "kind=folder requires the 'path' argument.",
                    """Retry with {"kind":"folder","path":"C:\\Some\\Folder"}.""");
            }

            if (!System.IO.Directory.Exists(folderPath))
            {
                throw WidgetLifecycle.Validation(
                    $"The folder path does not exist: {folderPath}",
                    "Create the folder first or pass an existing path.");
            }

            await widgetManager.CreateFolderWidgetAsync(folderPath).ConfigureAwait(true);
        }
        else if (kind.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            await widgetManager.CreateManagedWidgetAsync().ConfigureAwait(true);
        }
        else if (Enum.TryParse(kind, ignoreCase: true, out WidgetKind widgetKind))
        {
            widgetManager.EnsureFeatureWidgetEnabled(widgetKind);
            await widgetManager.CreateWidgetOfKindAsync(widgetKind).ConfigureAwait(true);
        }
        else
        {
            throw WidgetLifecycle.Validation(
                $"Unknown widget kind '{kind}'.",
                "Use one of: file, folder, todo, glance, music, weather, search.");
        }

        // Single-instance feature widgets (todo, glance, music, …) show the
        // existing instance instead of creating a second one; report that id
        // with created=false so clients can tell the two apart.
        HashSet<string> beforeIds = before.Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        WidgetCreatedResult result;
        string? newId = widgetManager.GetWidgetConfigSnapshot()
            .Select(config => config.Id)
            .FirstOrDefault(id => !beforeIds.Contains(id));
        if (newId is not null)
        {
            result = new WidgetCreatedResult(newId, kind, Created: true);
        }
        else if (isFeatureKind)
        {
            string? existingId = widgetManager.GetWidgetConfigSnapshot()
                .FirstOrDefault(config => config.WidgetKind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase))
                ?.Id;
            if (existingId is null)
            {
                throw WidgetLifecycle.Validation(
                    "Widget creation reported success but no widget appeared.",
                    "Call widgets/list to inspect the current widgets and retry if needed.");
            }

            result = new WidgetCreatedResult(existingId, kind, Created: false);
        }
        else
        {
            throw WidgetLifecycle.Validation(
                "Widget creation reported success but no new widget id appeared.",
                "Call widgets/list to inspect the current widgets and retry if needed.");
        }

        return JsonSerializer.SerializeToElement(result, WidgetLifecycleJsonContext.Default.WidgetCreatedResult);
    }

    private static string SerializeWidgetIds(WidgetManager widgetManager)
        => string.Join("|", widgetManager.GetWidgetConfigSnapshot().Select(config => config.Id).Order(StringComparer.Ordinal));
}

/// <summary>
/// Removes a widget by id. Always removes the widget only — managed folder
/// contents stay on disk (the destructive DeleteManagedFolder path is
/// deliberately not exposed). Flagged destructive: gated by the
/// allow-destructive-commands setting.
/// </summary>
public sealed class WidgetsRemoveHandler : ICommandHandler
{
    private readonly Func<WidgetManager?> _widgetManager;

    public WidgetsRemoveHandler(Func<WidgetManager?> widgetManager)
    {
        _widgetManager = widgetManager;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "widgets/remove",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.WidgetsWrite,
        MutatesState: true,
        Destructive: true,
        Summary: "Removes a widget by id. Managed folder contents stay on disk; this is gated by destructive-commands.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true,
                "Widget id (from widgets/list).", "\"3f2a\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":11,"method":"widgets/remove","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"3f2a"}}}""",
        ExampleResponseJson: """{"result":{"data":{"removed":true,"widgetId":"3f2a"}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        WidgetManager? widgetManager = _widgetManager()
            ?? throw WidgetLifecycle.NotLoaded("widget-manager", "DeskBox is still starting; retry shortly.");
        string widgetId = CommandArguments.RequireWidgetId(arguments);

        await widgetManager.RemoveWidgetAsync(
            widgetId,
            WidgetRemovalAction.RemoveWidgetOnly).ConfigureAwait(true);

        var result = new WidgetMutatedResult(widgetId, true, "removed");
        return JsonSerializer.SerializeToElement(result, WidgetLifecycleJsonContext.Default.WidgetMutatedResult);
    }
}

/// <summary>Shows a widget by id, lazily creating its window when needed.</summary>
public sealed class WidgetsShowHandler : ICommandHandler
{
    private readonly Func<WidgetManager?> _widgetManager;

    public WidgetsShowHandler(Func<WidgetManager?> widgetManager)
    {
        _widgetManager = widgetManager;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "widgets/show",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.WidgetsWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Shows a widget (creates its window if not loaded).",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Widget id.", "\"3f2a\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":12,"method":"widgets/show","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"3f2a"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"3f2a","shown":true}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        WidgetManager? widgetManager = _widgetManager()
            ?? throw WidgetLifecycle.NotLoaded("widget-manager", "DeskBox is still starting; retry shortly.");
        string widgetId = CommandArguments.RequireWidgetId(arguments);

        // Feature widgets (todo/glance/music/weather/search) are gated by a
        // session-level enabled flag; showing one through the generic path
        // would create a window that immediately closes again. Route through
        // CreateWidgetOfKindAsync instead, which enables the feature and
        // shows the singleton instance.
        string kind = widgetManager.GetWidgetConfigSnapshot()
            .FirstOrDefault(config => string.Equals(config.Id, widgetId, StringComparison.Ordinal))
            ?.WidgetKind.ToString() ?? string.Empty;

        bool shown;
        if (!string.IsNullOrEmpty(kind) && !kind.Equals("File", StringComparison.Ordinal))
        {
            if (!Enum.TryParse(kind, ignoreCase: true, out WidgetKind parsedKind))
            {
                throw WidgetLifecycle.Validation(
                    $"Widget '{widgetId}' has an unsupported kind '{kind}'.",
                    "Call widgets/list to inspect the widget.");
            }

            widgetManager.EnsureFeatureWidgetEnabled(parsedKind);
            await widgetManager.CreateWidgetOfKindAsync(parsedKind).ConfigureAwait(true);
            shown = true;
        }
        else
        {
            shown = await widgetManager.ShowWidgetAsync(widgetId).ConfigureAwait(true);
        }

        WidgetMutatedResult result = new(widgetId, shown, "shown");
        return JsonSerializer.SerializeToElement(result, WidgetLifecycleJsonContext.Default.WidgetMutatedResult);
    }
}

/// <summary>Hides a widget by id (its window must currently be loaded).</summary>
public sealed class WidgetsHideHandler : ICommandHandler
{
    private readonly Func<WidgetManager?> _widgetManager;

    public WidgetsHideHandler(Func<WidgetManager?> widgetManager)
    {
        _widgetManager = widgetManager;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "widgets/hide",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.WidgetsWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Hides a loaded widget.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Widget id.", "\"3f2a\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":13,"method":"widgets/hide","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"3f2a"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"3f2a","hidden":true}}}""");

    public Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        WidgetManager? widgetManager = _widgetManager()
            ?? throw WidgetLifecycle.NotLoaded("widget-manager", "DeskBox is still starting; retry shortly.");
        string widgetId = CommandArguments.RequireWidgetId(arguments);

        bool hidden = widgetManager.HideWidget(widgetId);
        var result = new WidgetMutatedResult(widgetId, true, "hidden");
        return Task.FromResult(JsonSerializer.SerializeToElement(result, WidgetLifecycleJsonContext.Default.WidgetMutatedResult));
    }
}

/// <summary>Renames a widget. For managed file widgets this also renames
/// the managed folder on disk.</summary>
public sealed class WidgetsRenameHandler : ICommandHandler
{
    private readonly Func<WidgetManager?> _widgetManager;

    public WidgetsRenameHandler(Func<WidgetManager?> widgetManager)
    {
        _widgetManager = widgetManager;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "widgets/rename",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.WidgetsWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Renames a widget (managed folder widgets rename their folder on disk too).",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "Widget id.", "\"3f2a\""),
            new CommandArgumentDescriptor("name", "string", true, "New display name.", "\"工作文件\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":14,"method":"widgets/rename","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"3f2a","name":"工作文件"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"3f2a","renamed":true}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        WidgetManager? widgetManager = _widgetManager()
            ?? throw WidgetLifecycle.NotLoaded("widget-manager", "DeskBox is still starting; retry shortly.");
        string widgetId = CommandArguments.RequireWidgetId(arguments);
        if (!CommandArguments.TryGetString(arguments, "name", out string name)
            || string.IsNullOrWhiteSpace(name))
        {
            throw WidgetLifecycle.Validation(
                "The 'name' argument is required and must be non-empty.",
                """Retry with {"widgetId":"<id>","name":"<new name>"}.""");
        }

        await widgetManager.RenameWidgetAsync(widgetId, name).ConfigureAwait(true);
        var result = new WidgetMutatedResult(widgetId, true, "renamed");
        return JsonSerializer.SerializeToElement(result, WidgetLifecycleJsonContext.Default.WidgetMutatedResult);
    }
}
