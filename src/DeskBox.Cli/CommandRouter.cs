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
            ("widgets", "create") => BuildWidgetsCreateCall(tokens),
            ("widgets", "remove") or ("widgets", "rm") => BuildWidgetsIdCall(tokens, "widgets/remove", requireYes: true),
            ("widgets", "show") => BuildWidgetsIdCall(tokens, "widgets/show"),
            ("widgets", "hide") => BuildWidgetsIdCall(tokens, "widgets/hide"),
            ("widgets", "rename") => BuildWidgetsRenameCall(tokens),
            ("quickcapture", "list") or ("qc", "list") => ("quickcapture/list", BuildQuickCaptureListArgs(tokens), false),
            ("quickcapture", "add") or ("qc", "add") => BuildQuickCaptureAddCall(tokens),
            ("quickcapture", "pin") or ("qc", "pin") => BuildQuickCapturePinCall(tokens),
            ("quickcapture", "update") or ("qc", "update") => BuildQuickCaptureUpdateCall(tokens),
            ("quickcapture", "delete") or ("qc", "rm") => BuildQuickCaptureDeleteCall(tokens),
            ("todo", "list") => ("todo/list", BuildTodoListArgs(tokens), false),
            ("todo", "add") => BuildTodoAddCall(tokens),
            ("todo", "done") => BuildTodoSetCompletedCall(tokens, isCompleted: true),
            ("todo", "reopen") => BuildTodoSetCompletedCall(tokens, isCompleted: false),
            ("todo", "edit") => BuildTodoEditCall(tokens),
            ("todo", "set-due") => BuildTodoSetDueCall(tokens),
            ("todo", "delete") or ("todo", "rm") => BuildTodoDeleteCall(tokens),
            ("todo", "clear-completed") => BuildTodoClearCompletedCall(tokens),
            ("files", "list") => ("files/list", BuildFilesListArgs(tokens), false),
            ("files", "add") => BuildFilesAddCall(tokens),
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

    private static (string Method, JsonElement Arguments, bool DryRun) BuildWidgetsCreateCall(
        IReadOnlyList<string> tokens)
    {
        string? kind = null;
        string? path = null;
        for (int i = 2; i < tokens.Count - 1; i++)
        {
            if (tokens[i] == "--kind" || tokens[i] == "kind")
            {
                kind = tokens[i + 1];
            }
            else if (tokens[i] == "--path" || tokens[i] == "path")
            {
                path = tokens[i + 1];
            }
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            // Positional fallback: first non-flag token after "widgets create".
            kind = tokens.Skip(2).FirstOrDefault(token => !token.StartsWith('-'));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            path = tokens.Skip(2).Skip(1).FirstOrDefault(token => !token.StartsWith('-'));
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox widgets create <file|folder|todo|glance|music|weather|search> [--path <folder>]");
        }

        if (kind.Equals("folder", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(path))
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox widgets create folder --path <existing folder>");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("kind", kind);
        if (!string.IsNullOrWhiteSpace(path))
        {
            writer.WriteString("path", path);
        }

        writer.WriteEndObject();
        writer.Flush();
        return ("widgets/create", JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), false);
    }

    private static (string Method, JsonElement Arguments, bool DryRun) BuildWidgetsIdCall(
        IReadOnlyList<string> tokens, string method, bool requireYes = false)
    {
        string? widgetId = tokens.Skip(2).FirstOrDefault(token => !token.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            throw new CliException(
                CliExitCode.UsageError,
                $"Usage: deskbox {method.Replace('/', ' ')} <widgetId>. Find ids with 'deskbox widgets list'.");
        }

        if (requireYes && !tokens.Contains("--yes", StringComparer.Ordinal))
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Refusing to remove a widget without --yes. Managed folder contents stay on disk, but the widget layout is removed.");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("widgetId", widgetId);
        writer.WriteEndObject();
        writer.Flush();
        return (method, JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), false);
    }

    private static (string Method, JsonElement Arguments, bool DryRun) BuildWidgetsRenameCall(
        IReadOnlyList<string> tokens)
    {
        string? widgetId = tokens.Skip(2).FirstOrDefault(token => !token.StartsWith('-'));
        string? name = tokens.Skip(3).FirstOrDefault(token => !token.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(widgetId) || string.IsNullOrWhiteSpace(name))
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox widgets rename <widgetId> <new name>");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("widgetId", widgetId);
        writer.WriteString("name", name);
        writer.WriteEndObject();
        writer.Flush();
        return ("widgets/rename", JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), false);
    }

    private static (string Method, JsonElement Arguments, bool DryRun) BuildQuickCapturePinCall(
        IReadOnlyList<string> tokens)
    {
        string? itemId = tokens.Skip(2).FirstOrDefault(token => !token.StartsWith('-'));
        bool unpin = tokens.Contains("--unpin", StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox quickcapture pin <itemId> [--unpin]");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("itemId", itemId);
        writer.WriteBoolean("pinned", !unpin);
        writer.WriteEndObject();
        writer.Flush();
        return ("quickcapture/pin", JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), false);
    }

    private static (string Method, JsonElement Arguments, bool DryRun) BuildQuickCaptureUpdateCall(
        IReadOnlyList<string> tokens)
    {
        string? itemId = tokens.Skip(2).FirstOrDefault(token => !token.StartsWith('-'));
        string body = string.Join(' ', tokens.Skip(3).Where(token => !token.StartsWith('-')));
        if (string.IsNullOrWhiteSpace(itemId) || body.Length == 0)
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox quickcapture update <itemId> <new body text>");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("itemId", itemId);
        writer.WriteString("body", body);
        writer.WriteEndObject();
        writer.Flush();
        return ("quickcapture/update", JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), false);
    }

    private static (string Method, JsonElement Arguments, bool DryRun) BuildQuickCaptureDeleteCall(
        IReadOnlyList<string> tokens)
    {
        List<string> ids = tokens.Skip(2).Where(token => !token.StartsWith('-')).ToList();
        if (ids.Count == 0)
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox quickcapture delete <itemId> [moreIds...] — permanent delete, no restore via the API.");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteStartArray("itemIds");
        foreach (string id in ids)
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return ("quickcapture/delete", JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), false);
    }

    private static (string Method, JsonElement Arguments, bool DryRun) BuildTodoSetCompletedCall(
        IReadOnlyList<string> tokens, bool isCompleted)
    {
        (string WidgetId, string ItemId) parsed = ParseWidgetAndItem(tokens);
        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("widgetId", parsed.WidgetId);
        writer.WriteString("itemId", parsed.ItemId);
        writer.WriteBoolean("isCompleted", isCompleted);
        writer.WriteEndObject();
        writer.Flush();
        return ("todo/set-completed", JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), false);
    }

    private static (string Method, JsonElement Arguments, bool DryRun) BuildTodoEditCall(
        IReadOnlyList<string> tokens)
    {
        string? widgetId = null;
        string? itemId = null;
        List<string> textParts = [];
        for (int i = 2; i < tokens.Count; i++)
        {
            if (tokens[i] == "--widget" && i + 1 < tokens.Count)
            {
                widgetId = tokens[++i];
            }
            else if (tokens[i].StartsWith('-'))
            {
                continue;
            }
            else if (itemId is null)
            {
                itemId = tokens[i];
            }
            else
            {
                textParts.Add(tokens[i]);
            }
        }

        string text = string.Join(' ', textParts);
        if (string.IsNullOrWhiteSpace(widgetId) || string.IsNullOrWhiteSpace(itemId) || text.Length == 0)
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox todo edit --widget <id> <itemId> <new text>");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("widgetId", widgetId);
        writer.WriteString("itemId", itemId);
        writer.WriteString("text", text);
        writer.WriteEndObject();
        writer.Flush();
        return ("todo/edit", JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), false);
    }

    private static (string Method, JsonElement Arguments, bool DryRun) BuildTodoSetDueCall(
        IReadOnlyList<string> tokens)
    {
        (string WidgetId, string ItemId) parsed = ParseWidgetAndItem(tokens);
        string? due = null;
        for (int i = 2; i < tokens.Count - 1; i++)
        {
            if (tokens[i] == "--due")
            {
                due = tokens[i + 1];
            }
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("widgetId", parsed.WidgetId);
        writer.WriteString("itemId", parsed.ItemId);
        if (!string.IsNullOrWhiteSpace(due))
        {
            writer.WriteString("dueDate", due);
        }

        writer.WriteEndObject();
        writer.Flush();
        return ("todo/set-due", JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), false);
    }

    private static (string Method, JsonElement Arguments, bool DryRun) BuildTodoDeleteCall(
        IReadOnlyList<string> tokens)
    {
        string? widgetId = null;
        List<string> itemIds = [];
        for (int i = 2; i < tokens.Count; i++)
        {
            if (tokens[i] == "--widget" && i + 1 < tokens.Count)
            {
                widgetId = tokens[++i];
            }
            else if (!tokens[i].StartsWith('-'))
            {
                itemIds.Add(tokens[i]);
            }
        }

        if (string.IsNullOrWhiteSpace(widgetId) || itemIds.Count == 0)
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox todo delete --widget <id> <itemId> [moreIds...]");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("widgetId", widgetId);
        writer.WriteStartArray("itemIds");
        foreach (string id in itemIds)
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return ("todo/delete", JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), false);
    }

    private static (string Method, JsonElement Arguments, bool DryRun) BuildTodoClearCompletedCall(
        IReadOnlyList<string> tokens)
    {
        string? widgetId = tokens.Skip(2).FirstOrDefault(token => !token.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox todo clear-completed --widget <id>");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("widgetId", widgetId);
        writer.WriteEndObject();
        writer.Flush();
        return ("todo/clear-completed", JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), false);
    }

    private static (string WidgetId, string ItemId) ParseWidgetAndItem(IReadOnlyList<string> tokens)
    {
        string? widgetId = null;
        string? itemId = null;
        for (int i = 2; i < tokens.Count; i++)
        {
            if (tokens[i] == "--widget" && i + 1 < tokens.Count)
            {
                widgetId = tokens[++i];
            }
            else if (!tokens[i].StartsWith('-') && itemId is null)
            {
                itemId = tokens[i];
            }
        }

        if (string.IsNullOrWhiteSpace(widgetId) || string.IsNullOrWhiteSpace(itemId))
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox todo <verb> --widget <widgetId> <itemId>. Find ids via todo list/widgets list.");
        }

        return (widgetId, itemId);
    }

    private static JsonElement BuildFilesListArgs(IReadOnlyList<string> tokens)
    {
        string? widgetId = tokens.Skip(2).FirstOrDefault(token => !token.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox files list <widgetId>. Find ids with 'deskbox widgets list'.");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("widgetId", widgetId);
        writer.WriteEndObject();
        writer.Flush();
        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    private static (string Method, JsonElement Arguments, bool DryRun) BuildFilesAddCall(
        IReadOnlyList<string> tokens)
    {
        string? widgetId = null;
        bool? move = null;
        List<string> paths = [];
        for (int i = 2; i < tokens.Count; i++)
        {
            if (tokens[i] == "--widget" && i + 1 < tokens.Count)
            {
                widgetId = tokens[++i];
            }
            else if (tokens[i] == "--move")
            {
                move = true;
            }
            else if (tokens[i] == "--copy")
            {
                move = false;
            }
            else if (!tokens[i].StartsWith('-'))
            {
                paths.Add(tokens[i]);
            }
        }

        if (string.IsNullOrWhiteSpace(widgetId) || paths.Count == 0)
        {
            throw new CliException(
                CliExitCode.UsageError,
                "Usage: deskbox files add --widget <widgetId> <path> [morePaths...] [--move|--copy]");
        }

        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        writer.WriteStartObject();
        writer.WriteString("widgetId", widgetId);
        writer.WriteStartArray("paths");
        foreach (string path in paths)
        {
            writer.WriteStringValue(Path.GetFullPath(path));
        }

        writer.WriteEndArray();
        if (move.HasValue)
        {
            writer.WriteBoolean("move", move.Value);
        }

        writer.WriteEndObject();
        writer.Flush();
        return ("files/add", JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), false);
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
            case "todo/set-completed" or "todo/edit" or "todo/set-due" or "todo/delete" or "todo/clear-completed":
                stdout.WriteLine($"{GetString(result.Data.GetValueOrDefault(), "action")} ✓  (widget {GetString(result.Data.GetValueOrDefault(), "widgetId")}, affected: {GetInt(result.Data.GetValueOrDefault(), "affectedCount")})");
                break;
            case "quickcapture/pin" or "quickcapture/update" or "quickcapture/delete":
                stdout.WriteLine($"{GetString(result.Data.GetValueOrDefault(), "action")} ✓  ({GetInt(result.Data.GetValueOrDefault(), "affectedCount")}/{GetInt(result.Data.GetValueOrDefault(), "requestedCount")} affected, saved={GetBool(result.Data.GetValueOrDefault(), "saved")})");
                break;
            case "widgets/create":
                stdout.WriteLine($"Created ✓  (id: {GetString(result.Data.GetValueOrDefault(), "widgetId")}, kind: {GetString(result.Data.GetValueOrDefault(), "kind")})");
                break;
            case "widgets/remove" or "widgets/show" or "widgets/hide" or "widgets/rename":
                stdout.WriteLine($"{GetString(result.Data.GetValueOrDefault(), "action")} ✓  (widget {GetString(result.Data.GetValueOrDefault(), "widgetId")})");
                break;
            case "files/list":
                PrintWidgetFiles(result, stdout);
                break;
            case "files/add":
                stdout.WriteLine($"Imported ✓  ({GetInt(result.Data.GetValueOrDefault(), "importedCount")} items into widget {GetString(result.Data.GetValueOrDefault(), "widgetId")}, moved={GetBool(result.Data.GetValueOrDefault(), "moved")})");
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
        stdout.WriteLine($"{GetInt(data, "count")} widget(s):");
        foreach (JsonElement widget in Enumerate(data, "widgets"))
        {
            stdout.WriteLine(
                $"  [{widget.GetProperty("id").GetString()}] {widget.GetProperty("kind").GetString()}  \"{widget.GetProperty("name").GetString()}\"  " +
                $"at ({widget.GetProperty("x").GetInt32()},{widget.GetProperty("y").GetInt32()}) {widget.GetProperty("width").GetInt32()}×{widget.GetProperty("height").GetInt32()}" +
                (widget.GetProperty("visible").GetBoolean() ? string.Empty : "  [hidden]")
                + (widget.TryGetProperty("mappedFolderPath", out JsonElement mapped) && mapped.ValueKind == JsonValueKind.String
                    ? $"  → {mapped.GetString()}"
                    : string.Empty));
        }
    }

    private static void PrintWidgetFiles(CommandResult result, TextWriter stdout)
    {
        JsonElement data = result.Data.GetValueOrDefault();
        stdout.WriteLine($"Widget {GetString(data, "widgetId")}: {GetInt(data, "count")} item(s)");
        foreach (JsonElement item in Enumerate(data, "items"))
        {
            string marker = item.GetProperty("isFolder").GetBoolean() ? "[D] " : "    ";
            stdout.WriteLine($"  {marker}{item.GetProperty("name").GetString()}  ({item.GetProperty("path").GetString()})");
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
