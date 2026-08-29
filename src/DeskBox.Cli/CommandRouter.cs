using System.Text.Json;
using DeskBox.Protocol;

namespace DeskBox.Cli;

/// <summary>
/// Maps CLI verbs onto command API methods. The CLI is deliberately a thin
/// translation layer: every verb becomes one JSON-RPC call, and human output
/// is a formatter over the same JSON that --json prints.
/// </summary>
public static class CommandRouter
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> tokens,
        PipeRpcClient client,
        string clientVersion,
        bool jsonOutput,
        TextWriter stdout,
        TextWriter stderr)
    {
        string verb = tokens[0].ToLowerInvariant();
        string sub = tokens.Count > 1 ? tokens[1].ToLowerInvariant() : string.Empty;

        (string method, JsonElement arguments, bool dryRun) = (verb, sub) switch
        {
            ("ping", _) => ("server/ping", JsonDocument.Parse("{}").RootElement.Clone(), false),
            ("info", _) => ("server/info", JsonDocument.Parse("{}").RootElement.Clone(), false),
            ("schema", _) => ("server/schema", JsonDocument.Parse("{}").RootElement.Clone(), false),
            ("settings", "get") => ("settings/get", JsonDocument.Parse("{}").RootElement.Clone(), false),
            ("widgets", "list") or ("widgets", "ls") => ("widgets/list", JsonDocument.Parse("{}").RootElement.Clone(), false),
            ("quickcapture", "list") or ("qc", "list") => ("quickcapture/list", BuildQuickCaptureListArgs(tokens), false),
            ("quickcapture", "add") or ("qc", "add") => BuildQuickCaptureAddCall(tokens),
            ("todo", "list") => ("todo/list", BuildTodoListArgs(tokens), false),
            ("todo", "add") => BuildTodoAddCall(tokens),
            _ => throw new CliException(
                CliExitCode.UsageError,
                $"Unknown command '{verb}{(sub.Length > 0 ? " " + sub : string.Empty)}'. Run 'deskbox --help' for usage."),
        };

        JsonRpcResponse response = await client
            .SendAsync(method, arguments, clientVersion, dryRun: dryRun)
            .ConfigureAwait(false);

        if (response.Error is not null)
        {
            HumanFormatter.PrintError(response.Error, stderr, jsonOutput);
            return (int)CliExitCode.ServerRejected;
        }

        if (jsonOutput || response.Result is null)
        {
            stdout.WriteLine(CommandApiJson.SerializeResponse(response));
            return (int)CliExitCode.Ok;
        }

        HumanFormatter.Print(method, response.Result, stdout);
        return (int)CliExitCode.Ok;
    }

    private static JsonElement BuildQuickCaptureListArgs(IReadOnlyList<string> tokens)
    {
        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        for (int i = 2; i < tokens.Count - 1; i++)
        {
            if (tokens[i] == "--limit" && int.TryParse(tokens[i + 1], out int limit))
            {
                writer.WriteNumber("limit", limit);
                i++;
            }
        }

        writer.WriteEndObject();
        writer.Flush();
        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    private static (string Method, JsonElement Arguments, bool DryRun) BuildQuickCaptureAddCall(
        IReadOnlyList<string> tokens)
    {
        string body = string.Empty;
        string? title = null;
        bool pin = false;
        bool dryRun = false;
        List<string> rest = [];
        for (int i = 2; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case "--title" when i + 1 < tokens.Count:
                    title = tokens[++i];
                    break;
                case "--pin":
                    pin = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    rest.Add(tokens[i]);
                    break;
            }
        }

        body = string.Join(' ', rest);
        if (body.Length == 0)
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox quickcapture add <body> [--title <title>] [--pin] [--dry-run]");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("body", body);
        if (title is not null)
        {
            writer.WriteString("title", title);
        }

        if (pin)
        {
            writer.WriteBoolean("pin", true);
        }

        writer.WriteEndObject();
        writer.Flush();
        return ("quickcapture/add", JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), dryRun);
    }

    private static JsonElement BuildTodoListArgs(IReadOnlyList<string> tokens)
    {
        string? widgetId = null;
        int? limit = null;
        for (int i = 2; i < tokens.Count - 1; i++)
        {
            if (tokens[i] == "--widget")
            {
                widgetId = tokens[i + 1];
            }
            else if (tokens[i] == "--limit" && int.TryParse(tokens[i + 1], out int limitValue))
            {
                limit = limitValue;
            }
        }

        if (string.IsNullOrWhiteSpace(widgetId))
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox todo list --widget <widgetId> [--limit N]. Find widget ids with 'deskbox widgets list'.");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("widgetId", widgetId);
        if (limit.HasValue)
        {
            writer.WriteNumber("limit", limit.Value);
        }

        writer.WriteEndObject();
        writer.Flush();
        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    private static (string Method, JsonElement Arguments, bool DryRun) BuildTodoAddCall(
        IReadOnlyList<string> tokens)
    {
        string? widgetId = null;
        bool important = false;
        bool dryRun = false;
        string? colorMarker = null;
        List<string> rest = [];
        for (int i = 2; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case "--widget" when i + 1 < tokens.Count:
                    widgetId = tokens[++i];
                    break;
                case "--important":
                    important = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--color" when i + 1 < tokens.Count:
                    colorMarker = tokens[++i];
                    break;
                default:
                    rest.Add(tokens[i]);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(widgetId))
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox todo add --widget <widgetId> <text> [--important] [--color <marker>] [--dry-run]");
        }

        string text = string.Join(' ', rest);
        if (text.Length == 0)
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox todo add --widget <widgetId> <text> [--important] [--color <marker>] [--dry-run]");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("widgetId", widgetId);
        writer.WriteString("text", text);
        if (important)
        {
            writer.WriteBoolean("important", true);
        }

        if (colorMarker is not null)
        {
            writer.WriteString("colorMarker", colorMarker);
        }

        writer.WriteEndObject();
        writer.Flush();
        return ("todo/add", JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), dryRun);
    }
}

/// <summary>Human-readable rendering of command results.</summary>
public static class HumanFormatter
{
    public static void Print(string method, CommandResult result, TextWriter stdout)
    {
        switch (method)
        {
            case "server/ping":
                stdout.WriteLine("DeskBox is running. ✓");
                break;
            case "server/info":
                PrintServerInfo(result, stdout);
                break;
            case "server/schema":
                PrintSchema(result, stdout);
                break;
            case "widgets/list":
                PrintWidgets(result, stdout);
                break;
            case "settings/get":
                stdout.WriteLine(result.Data.GetValueOrDefault().ToString());
                break;
            case "quickcapture/list":
                PrintQuickCaptureList(result, stdout);
                break;
            case "quickcapture/add":
                stdout.WriteLine($"Saved ✓  (id: {GetString(result.Data.GetValueOrDefault(), "id")})");
                break;
            case "todo/list":
                PrintTodoList(result, stdout);
                break;
            case "todo/add":
                stdout.WriteLine($"Added ✓  (widget {GetString(result.Data.GetValueOrDefault(), "widgetId")}, {GetInt(result.Data.GetValueOrDefault(), "itemCount")} items total)");
                break;
            default:
                stdout.WriteLine(result.Data.GetValueOrDefault().ToString());
                break;
        }
    }

    private static void PrintServerInfo(CommandResult result, TextWriter stdout)
    {
        JsonElement data = result.Data.GetValueOrDefault();
        stdout.WriteLine($"DeskBox {GetString(data, "serverVersion")}  (protocol v{GetInt(data, "protocolVersion")}, up {GetInt(data, "uptimeSeconds")}s)");
        stdout.WriteLine($"Commands: {GetInt(data, "commandCount")}   ReadOnly: {GetBool(data, "readOnlyMode")}   Destructive allowed: {GetBool(data, "destructiveAllowed")}");
        stdout.WriteLine("Capabilities:");
        foreach (JsonElement capability in Enumerate(data, "capabilities"))
        {
            stdout.WriteLine($"  - {capability.GetString()}");
        }
    }

    private static void PrintSchema(CommandResult result, TextWriter stdout)
    {
        JsonElement data = result.Data.GetValueOrDefault();
        stdout.WriteLine($"Command API schema v{GetInt(data, "protocolVersion")} — server {GetString(data, "serverVersion")}");
        stdout.WriteLine($"Capabilities: {string.Join(", ", Enumerate(data, "capabilities").Select(c => c.GetString()))}");
        stdout.WriteLine();
        foreach (JsonElement command in Enumerate(data, "commands"))
        {
            stdout.WriteLine($"{command.GetProperty("method").GetString()}  [{GetString(command, "category")}]{(GetBool(command, "mutatesState") ? " [write]" : string.Empty)}{(GetBool(command, "destructive") ? " [destructive]" : string.Empty)}");
            stdout.WriteLine($"    {GetString(command, "summary")}");
            foreach (JsonElement argument in Enumerate(command, "arguments"))
            {
                string required = argument.GetProperty("required").GetBoolean() ? " (required)" : string.Empty;
                string description = argument.TryGetProperty("description", out JsonElement descriptionElement)
                    && descriptionElement.ValueKind == JsonValueKind.String
                    ? descriptionElement.GetString() ?? string.Empty
                    : string.Empty;
                stdout.WriteLine($"    --{argument.GetProperty("name").GetString()} <{argument.GetProperty("type").GetString()}>{required}: {description}");
            }

            stdout.WriteLine();
        }
    }

    private static void PrintWidgets(CommandResult result, TextWriter stdout)
    {
        JsonElement data = result.Data.GetValueOrDefault();
        stdout.WriteLine($"{GetInt(data, "count")} widget window(s):");
        foreach (JsonElement widget in Enumerate(data, "widgets"))
        {
            stdout.WriteLine(
                $"  hwnd={widget.GetProperty("hwnd").GetInt64()}  \"{widget.GetProperty("title").GetString()}\"  [{widget.GetProperty("className").GetString()}]  " +
                $"at ({widget.GetProperty("x").GetInt32()},{widget.GetProperty("y").GetInt32()}) {widget.GetProperty("width").GetInt32()}×{widget.GetProperty("height").GetInt32()}" +
                (widget.GetProperty("visible").GetBoolean() ? string.Empty : "  [hidden]"));
        }
    }

    private static void PrintQuickCaptureList(CommandResult result, TextWriter stdout)
    {
        JsonElement data = result.Data.GetValueOrDefault();
        stdout.WriteLine($"{GetInt(data, "totalCount")} quick capture item(s):");
        foreach (JsonElement item in Enumerate(data, "items"))
        {
            string title = item.TryGetProperty("title", out JsonElement titleElement) && titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString() + " — "
                : string.Empty;
            string body = item.GetProperty("body").GetString() ?? string.Empty;
            if (body.Length > 80)
            {
                body = body[..80] + "…";
            }

            stdout.WriteLine($"  [{item.GetProperty("id").GetString()}] {(item.GetProperty("isPinned").GetBoolean() ? "📌 " : string.Empty)}{title}{body}");
        }
    }

    private static void PrintTodoList(CommandResult result, TextWriter stdout)
    {
        JsonElement data = result.Data.GetValueOrDefault();
        stdout.WriteLine($"Widget {GetString(data, "widgetId")}: {GetInt(data, "totalCount")} todo item(s):");
        foreach (JsonElement item in Enumerate(data, "items"))
        {
            string marker = item.GetProperty("isCompleted").GetBoolean() ? "☑" : "☐";
            string important = item.GetProperty("isImportant").GetBoolean() ? " ⭐" : string.Empty;
            string body = item.GetProperty("text").GetString() ?? string.Empty;
            stdout.WriteLine($"  {marker} [{item.GetProperty("id").GetString()}] {body}{important}");
        }
    }

    public static void PrintError(JsonRpcErrorObject error, TextWriter stderr, bool jsonOutput)
    {
        if (jsonOutput)
        {
            stderr.WriteLine(CommandApiJson.SerializeResponse(new JsonRpcResponse { Error = error }));
            return;
        }

        stderr.WriteLine($"server error [{error.Data?.Code} / {error.Data?.Phase}]: {error.Message}");
        if (!string.IsNullOrWhiteSpace(error.Data?.Hint))
        {
            stderr.WriteLine($"hint: {error.Data.Hint}");
        }
    }

    private static IEnumerable<JsonElement> Enumerate(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out JsonElement array)
            && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];

    private static string GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            ? (int)value.GetDouble()
            : 0;

    private static bool GetBool(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.True;
}
