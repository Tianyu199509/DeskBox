using System.Text;
using System.Text.Json;
using DeskBox.Protocol;

namespace DeskBox.Cli;

/// <summary>
/// Minimal Model Context Protocol server over stdio (LSP-style
/// Content-Length framing). Maps a small set of coarse-grained tools onto
/// the DeskBox command API, so MCP hosts (Claude Desktop, Cursor, ...) can
/// drive DeskBox without any bespoke integration.
///
/// stdout carries protocol frames only; diagnostics go to stderr.
/// </summary>
public sealed class McpServer
{
    private const string McpProtocolVersion = "2024-11-05";
    private const string ServerName = "deskbox";

    private readonly PipeRpcClient _client;
    private readonly string _clientVersion;
    private readonly Stream _input;
    private readonly Stream _output;

    public McpServer(PipeRpcClient client, string clientVersion, Stream? input = null, Stream? output = null)
    {
        _client = client;
        _clientVersion = clientVersion;
        _input = input ?? Console.OpenStandardInput();
        _output = output ?? Console.OpenStandardOutput();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                await WriteErrorAsync(null, JsonRpcErrorCodes.ParseError, $"MCP frame is not valid JSON: {ex.Message}").ConfigureAwait(false);
                continue;
            }

            using (document)
            {
                await HandleMessageAsync(document.RootElement.Clone(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleMessageAsync(JsonElement message, CancellationToken cancellationToken)
    {
        string method = message.TryGetProperty("method", out JsonElement methodElement)
            && methodElement.ValueKind == JsonValueKind.String
            ? methodElement.GetString() ?? string.Empty
            : string.Empty;
        JsonElement id = message.TryGetProperty("id", out JsonElement idElement)
            ? idElement.Clone()
            : JsonSerializer.SerializeToElement<JsonElement?>(null);

        // Notifications (no id) never get a response.
        if (id.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        switch (method)
        {
            case "initialize":
                await WriteResultAsync(id, new
                {
                    protocolVersion = McpProtocolVersion,
                    capabilities = new
                    {
                        tools = new { listChanged = false },
                    },
                    serverInfo = new
                    {
                        name = ServerName,
                        version = _clientVersion,
                        instructions = "Controls a running DeskBox desktop-organization app on this machine. "
                            + "Call deskbox_status first to discover capabilities. "
                            + "Organize/list tools are read-only; add tools mutate local data.",
                    },
                }).ConfigureAwait(false);
                break;

            case "tools/list":
                await WriteResultAsync(id, new { tools = ToolRegistry.BuildTools() }).ConfigureAwait(false);
                break;

            case "tools/call":
                await CallToolAsync(id, message, cancellationToken).ConfigureAwait(false);
                break;

            case "ping":
                await WriteResultAsync(id, new { }).ConfigureAwait(false);
                break;

            default:
                await WriteErrorAsync(id, JsonRpcErrorCodes.MethodNotFound, $"MCP method '{method}' is not supported.").ConfigureAwait(false);
                break;
        }
    }

    private async Task CallToolAsync(JsonElement id, JsonElement message, CancellationToken cancellationToken)
    {
        string toolName = string.Empty;
        JsonElement arguments = JsonSerializer.SerializeToElement(new { });
        if (message.TryGetProperty("params", out JsonElement parameters))
        {
            if (parameters.TryGetProperty("name", out JsonElement nameElement)
                && nameElement.ValueKind == JsonValueKind.String)
            {
                toolName = nameElement.GetString() ?? string.Empty;
            }

            if (parameters.TryGetProperty("arguments", out JsonElement argsElement)
                && argsElement.ValueKind == JsonValueKind.Object)
            {
                arguments = argsElement.Clone();
            }
        }

        (string rpcMethod, JsonElement rpcArguments) = ToolRegistry.MapToolCall(toolName, arguments);
        if (rpcMethod.Length == 0)
        {
            await WriteErrorAsync(id, JsonRpcErrorCodes.InvalidParams, $"Unknown tool '{toolName}'. Call tools/list first.").ConfigureAwait(false);
            return;
        }

        JsonRpcResponse response;
        try
        {
            response = await _client
                .SendAsync(rpcMethod, rpcArguments, _clientVersion, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CliException ex)
        {
            await WriteToolErrorAsync(id, ex.Message).ConfigureAwait(false);
            return;
        }

        if (response.Error is not null)
        {
            string message_ = response.Error.Message;
            string? hint = response.Error.Data?.Hint;
            await WriteToolErrorAsync(id, hint is null ? message_ : $"{message_} (hint: {hint})").ConfigureAwait(false);
            return;
        }

        await WriteResultAsync(id, new
        {
            content = new object[]
            {
                new { type = "text", text = CommandApiJson.SerializeResponse(response) },
            },
        }).ConfigureAwait(false);
    }

    private async Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        // MCP stdio framing: "Content-Length: <n>\r\n\r\n" + n bytes of JSON.
        StringBuilder header = new();
        while (true)
        {
            int c = _input.ReadByte();
            if (c == -1)
            {
                return null;
            }

            header.Append((char)c);
            if (header.Length >= 4 && header.ToString()[^4..] == "\r\n\r\n")
            {
                break;
            }
        }

        int contentLength = ParseContentLength(header.ToString());
        if (contentLength <= 0 || contentLength > CommandApiProtocol.MaxFrameBytes)
        {
            throw new IOException($"Invalid MCP frame length: {contentLength}.");
        }

        byte[] payload = new byte[contentLength];
        int totalRead = 0;
        while (totalRead < contentLength)
        {
            int read = await _input.ReadAsync(payload.AsMemory(totalRead..), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            totalRead += read;
        }

        return Encoding.UTF8.GetString(payload);
    }

    private static int ParseContentLength(string header)
    {
        foreach (string headerLine in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = headerLine.IndexOf(':');
            if (colon > 0
                && headerLine[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(headerLine[(colon + 1)..].Trim(), out int length))
            {
                return length;
            }
        }

        return -1;
    }

    private Task WriteResultAsync(JsonElement id, object result)
        => WriteFrameAsync(new { jsonrpc = "2.0", id, result });

    private Task WriteErrorAsync(JsonElement? id, int code, string message)
        => WriteFrameAsync(new { jsonrpc = "2.0", id, error = new { code, message } });

    private Task WriteToolErrorAsync(JsonElement id, string message)
        => WriteFrameAsync(new
        {
            jsonrpc = "2.0",
            id,
            result = new
            {
                content = new object[] { new { type = "text", text = message } },
                isError = true,
            },
        });

    private async Task WriteFrameAsync(object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] header = Encoding.UTF8.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _output.WriteAsync(header).ConfigureAwait(false);
        await _output.WriteAsync(body).ConfigureAwait(false);
        await _output.FlushAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Static MCP tool surface. Tools are coarse-grained on purpose: LLMs call
/// few well-described tools far more reliably than many narrow ones.
/// </summary>
public static class ToolRegistry
{
    public static object[] BuildTools() =>
    [
        new
        {
            name = "deskbox_status",
            description = "Check DeskBox is running and return version, uptime, capabilities, and command API policy.",
            inputSchema = new { type = "object", properties = new { }, required = Array.Empty<string>() },
        },
        new
        {
            name = "list_widgets",
            description = "List live DeskBox widget windows with ids, titles, class names, and screen rectangles.",
            inputSchema = new { type = "object", properties = new { }, required = Array.Empty<string>() },
        },
        new
        {
            name = "list_quick_capture",
            description = "List the user's quick capture notes (newest and pinned first).",
            inputSchema = new
            {
                type = "object",
                properties = new { limit = new { type = "integer", description = "Max items to return (1-200, default 50)." } },
                required = Array.Empty<string>(),
            },
        },
        new
        {
            name = "add_quick_capture",
            description = "Add a text note to the user's quick capture widget.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    body = new { type = "string", description = "Note text." },
                    title = new { type = "string", description = "Optional title." },
                    pin = new { type = "boolean", description = "Pin the note (default false)." },
                },
                required = new[] { "body" },
            },
        },
        new
        {
            name = "list_todos",
            description = "List items of one todo widget. Get widget ids from list_widgets.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    widgetId = new { type = "string", description = "Todo widget id." },
                    limit = new { type = "integer", description = "Max items (1-500, default 100)." },
                },
                required = new[] { "widgetId" },
            },
        },
        new
        {
            name = "add_todo",
            description = "Add one todo item to a todo widget.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    widgetId = new { type = "string", description = "Todo widget id." },
                    text = new { type = "string", description = "Todo text." },
                    important = new { type = "boolean", description = "Mark important (default false)." },
                    colorMarker = new { type = "string", description = "red|orange|yellow|green|blue|purple|teal|pink" },
                },
                required = new[] { "widgetId", "text" },
            },
        },
        new
        {
            name = "complete_todo",
            description = "Mark one todo item completed (recurring items generate their next occurrence).",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    widgetId = new { type = "string", description = "Todo widget id." },
                    itemId = new { type = "string", description = "Item id from list_todos." },
                    isCompleted = new { type = "boolean", description = "Target state (default true)." },
                },
                required = new[] { "widgetId", "itemId" },
            },
        },
        new
        {
            name = "delete_todos",
            description = "Delete one or more todo items by id.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    widgetId = new { type = "string", description = "Todo widget id." },
                    itemIds = new { type = "array", items = new { type = "string" }, description = "Item ids to delete." },
                },
                required = new[] { "widgetId", "itemIds" },
            },
        },
        new
        {
            name = "list_widget_files",
            description = "List the file/folder entries currently shown in one file widget.",
            inputSchema = new
            {
                type = "object",
                properties = new { widgetId = new { type = "string", description = "File widget id." } },
                required = new[] { "widgetId" },
            },
        },
        new
        {
            name = "add_files_to_widget",
            description = "Import (copy or move) files/folders into one file widget's mapped folder.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    widgetId = new { type = "string", description = "File widget id." },
                    paths = new { type = "array", items = new { type = "string" }, description = "Absolute source paths." },
                    move = new { type = "boolean", description = "true = move sources; false = copy; omit to follow the app setting." },
                },
                required = new[] { "widgetId", "paths" },
            },
        },
        new
        {
            name = "create_widget",
            description = "Create a widget: file (managed storage), folder (maps an existing path), or a feature widget (todo|glance|music|weather|search).",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", description = "file|folder|todo|glance|music|weather|search" },
                    path = new { type = "string", description = "Folder path (required for kind=folder)." },
                },
                required = new[] { "kind" },
            },
        },
        new
        {
            name = "search_desktop",
            description = "Search files (via Everything) and DeskBox content (notes, todos, widget titles).",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Search text." },
                    limit = new { type = "integer", description = "Max results (1-50, default 20)." },
                },
                required = new[] { "query" },
            },
        },
        new
        {
            name = "organize_desktop",
            description = "Desktop organization. action=plan returns a preview (nothing moves); action=apply executes a returned planId; action=undo rolls back a historyId.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    action = new { type = "string", description = "plan|apply|undo", enumValues = new[] { "plan", "apply", "undo" } },
                    planId = new { type = "string", description = "Required for apply." },
                    historyId = new { type = "string", description = "Required for undo." },
                },
                required = new[] { "action" },
            },
        },
        new
        {
            name = "set_appearance",
            description = "Set an allowlisted appearance setting: theme (System|Light|Dark) or language.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    key = new { type = "string", description = "theme|language" },
                    value = new { type = "string", description = "New value, e.g. Dark or en-US." },
                },
                required = new[] { "key", "value" },
            },
        },
        new
        {
            name = "music_control",
            description = "Control the system media session through the music widget (SMTC): read status, toggle play/pause, next/previous track, set system volume.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    action = new { type = "string", description = "status|toggle|next|previous|volume" },
                    widgetId = new { type = "string", description = "Music widget id (from list_widgets)." },
                    volume = new { type = "integer", description = "System volume percent 0-100 (for action=volume)." },
                },
                required = new[] { "action", "widgetId" },
            },
        },
        new
        {
            name = "get_weather",
            description = "Fetch current weather for the configured location (MSN with Open-Meteo fallback).",
            inputSchema = new
            {
                type = "object",
                properties = new { forceRefresh = new { type = "boolean", description = "Bypass the 30-minute cache." } },
                required = Array.Empty<string>(),
            },
        },
        new
        {
            name = "set_weather_city",
            description = "Geocode a city name and switch the weather location (all weather widgets refresh).",
            inputSchema = new
            {
                type = "object",
                properties = new { city = new { type = "string", description = "City name." } },
                required = new[] { "city" },
            },
        },
        new
        {
            name = "glance_control",
            description = "Control a glance photo widget: read its settings, advance to the next image, or toggle auto-rotation pause.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    action = new { type = "string", description = "get|next|toggle-pause" },
                    widgetId = new { type = "string", description = "Glance widget id (from list_widgets)." },
                },
                required = new[] { "action", "widgetId" },
            },
        },
        new
        {
            name = "get_settings",
            description = "Read an allowlisted snapshot of DeskBox settings.",
            inputSchema = new { type = "object", properties = new { }, required = Array.Empty<string>() },
        },
    ];

    /// <summary>Maps one MCP tool call to its command API method and arguments.</summary>
    public static (string Method, JsonElement Arguments) MapToolCall(string toolName, JsonElement arguments)
    {
        switch (toolName)
        {
            case "deskbox_status":
                return ("server/info", JsonDocument.Parse("{}").RootElement.Clone());
            case "list_widgets":
                return ("widgets/list", JsonDocument.Parse("{}").RootElement.Clone());
            case "get_settings":
                return ("settings/get", JsonDocument.Parse("{}").RootElement.Clone());
            case "list_quick_capture":
                return ("quickcapture/list", arguments);
            case "add_quick_capture":
                return ("quickcapture/add", arguments);
            case "list_todos":
                return ("todo/list", arguments);
            case "add_todo":
                return ("todo/add", arguments);
            case "complete_todo":
                return ("todo/set-completed", arguments);
            case "delete_todos":
                return ("todo/delete", arguments);
            case "list_widget_files":
                return ("files/list", arguments);
            case "add_files_to_widget":
                return ("files/add", arguments);
            case "create_widget":
                return ("widgets/create", arguments);
            case "search_desktop":
                return ("search/query", arguments);
            case "organize_desktop":
            {
                string action = arguments.ValueKind == JsonValueKind.Object
                    && arguments.TryGetProperty("action", out JsonElement actionElement)
                    && actionElement.ValueKind == JsonValueKind.String
                    ? actionElement.GetString() ?? string.Empty
                    : string.Empty;
                return action switch
                {
                    "plan" => ("organize/plan", JsonSerializer.SerializeToElement(new { })),
                    "apply" => ("organize/apply", arguments),
                    "undo" => ("organize/undo", arguments),
                    _ => (string.Empty, arguments),
                };
            }
            case "set_appearance":
                return ("settings/set", arguments);
            case "music_control":
            {
                string action = arguments.ValueKind == JsonValueKind.Object
                    && arguments.TryGetProperty("action", out JsonElement musicAction)
                    && musicAction.ValueKind == JsonValueKind.String
                    ? musicAction.GetString() ?? string.Empty
                    : string.Empty;
                return action switch
                {
                    "status" => ("music/status", arguments),
                    "toggle" => ("music/toggle", arguments),
                    "next" => ("music/next", arguments),
                    "previous" => ("music/previous", arguments),
                    "volume" => ("music/volume", arguments),
                    _ => (string.Empty, arguments),
                };
            }
            case "get_weather":
                return ("weather/get", arguments);
            case "set_weather_city":
                return ("weather/set-city", arguments);
            case "glance_control":
            {
                string action = arguments.ValueKind == JsonValueKind.Object
                    && arguments.TryGetProperty("action", out JsonElement glanceAction)
                    && glanceAction.ValueKind == JsonValueKind.String
                    ? glanceAction.GetString() ?? string.Empty
                    : string.Empty;
                return action switch
                {
                    "get" => ("glance/get", arguments),
                    "next" => ("glance/next", arguments),
                    "toggle-pause" => ("glance/toggle-pause", arguments),
                    _ => (string.Empty, arguments),
                };
            }
            default:
                return (string.Empty, arguments);
        }
    }
}
