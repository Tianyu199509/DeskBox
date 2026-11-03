using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DeskBox.Services;

/// <summary>
/// Opt-in, strictly loopback-bound (<c>127.0.0.1</c>) HTTP/1.1 bridge exposing:
/// 1. Standard MCP Streamable HTTP endpoint (<c>POST /mcp</c>) for Cherry Studio, WSL2 agents,
///    Dify, n8n, LobeChat, and remote/container MCP clients.
/// 2. Zero-dependency REST/Webhook endpoints (<c>/api/v1/...</c>) for PowerShell, curl, Python,
///    Git hooks, and automation scripts.
/// Protected by a mandatory per-user Bearer token (<c>Authorization: Bearer &lt;token&gt;</c> or
/// <c>X-DeskBox-Token: &lt;token&gt;</c>) to prevent browser localhost CSRF/SSRF.
/// </summary>
public sealed class DeskBoxAgentHttpBridgeServer : IAsyncDisposable
{
    private const int MaxHeaderLineBytes = 16 * 1024;
    private const int MaxHeaderCount = 64;
    private const int MaxRequestBodyBytes = 2 * 1024 * 1024;

    private readonly int _requestedPort;
    private readonly Func<string> _tokenProvider;
    private readonly Func<string, CancellationToken, Task<string>> _bridgeJsonDispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _clientTasks = [];
    private readonly object _gate = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private volatile bool _disposed;

    public DeskBoxAgentHttpBridgeServer(
        int port,
        Func<string> tokenProvider,
        Func<string, CancellationToken, Task<string>> bridgeJsonDispatcher)
    {
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(bridgeJsonDispatcher);

        _requestedPort = port;
        _tokenProvider = tokenProvider;
        _bridgeJsonDispatcher = bridgeJsonDispatcher;
    }

    public bool IsRunning { get; private set; }

    public int ActualPort { get; private set; }

    public string? LastBindError { get; private set; }

    public string BaseUrl => ActualPort > 0 ? $"http://127.0.0.1:{ActualPort}" : string.Empty;

    public string McpEndpointUrl => ActualPort > 0 ? $"http://127.0.0.1:{ActualPort}/mcp" : string.Empty;

    public string RestPushUrl => ActualPort > 0 ? $"http://127.0.0.1:{ActualPort}/api/v1/widgets/push" : string.Empty;

    public bool TryStart(out string? errorMessage)
    {
        if (_disposed)
        {
            errorMessage = "Server is disposed.";
            return false;
        }

        if (IsRunning && _listener is not null)
        {
            errorMessage = null;
            return true;
        }

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, _requestedPort);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start(backlog: 32);

            _listener = listener;
            ActualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            IsRunning = true;
            LastBindError = null;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            ActualPort = 0;
            LastBindError = ex.Message;
            errorMessage = ex.Message;
            return false;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            TcpClient? tcpClient = null;
            try
            {
                tcpClient = await listener.AcceptTcpClientAsync(cancellationToken);
                TcpClient accepted = tcpClient;
                tcpClient = null;

                Task worker = Task.Run(() => HandleTcpClientAsync(accepted, cancellationToken));
                lock (_gate)
                {
                    _clientTasks.RemoveAll(static t => t.IsCompleted);
                    _clientTasks.Add(worker);
                }
            }
            catch (OperationCanceledException)
            {
                tcpClient?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                tcpClient?.Dispose();
                break;
            }
            catch (SocketException) when (_disposed || cancellationToken.IsCancellationRequested)
            {
                tcpClient?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                tcpClient?.Dispose();
                App.Log($"[AgentHttpBridge] Accept error: {ex.Message}");
                try
                {
                    await Task.Delay(150, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken serverCt)
    {
        using (client)
        {
            client.NoDelay = true;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
            CancellationToken ct = timeoutCts.Token;

            try
            {
                await using NetworkStream stream = client.GetStream();
                ParsedHttpRequest? req = await TryReadHttpRequestAsync(stream, ct);
                if (req is null)
                {
                    return;
                }

                if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(
                        stream,
                        statusCode: 204,
                        statusText: "No Content",
                        contentType: "text/plain; charset=utf-8",
                        body: [],
                        extraHeader: "Allow: GET, POST, DELETE, OPTIONS\r\nAccess-Control-Allow-Methods: GET, POST, DELETE, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type, Authorization, X-DeskBox-Token, Mcp-Session-Id, MCP-Protocol-Version",
                        ct);
                    return;
                }

                if (!IsAuthorized(req.Headers))
                {
                    byte[] unauthBytes = Encoding.UTF8.GetBytes(
                        """{"ok":false,"error":"Unauthorized. Provide header 'Authorization: Bearer <token>' or 'X-DeskBox-Token: <token>'."}""");
                    await WriteResponseAsync(
                        stream,
                        statusCode: 401,
                        statusText: "Unauthorized",
                        contentType: "application/json; charset=utf-8",
                        body: unauthBytes,
                        extraHeader: "WWW-Authenticate: Bearer realm=\"DeskBox\"",
                        ct);
                    return;
                }

                await RouteAuthenticatedRequestAsync(stream, req, ct);
            }
            catch
            {
                // Ignore client socket disconnects or timeouts
            }
        }
    }

    private bool IsAuthorized(IReadOnlyDictionary<string, string> headers)
    {
        string expected = _tokenProvider()?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        string candidate = string.Empty;
        if (headers.TryGetValue("Authorization", out string? authVal) && !string.IsNullOrWhiteSpace(authVal))
        {
            string trimmed = authVal.Trim();
            if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                candidate = trimmed["Bearer ".Length..].Trim();
            }
            else
            {
                candidate = trimmed;
            }
        }
        else if (headers.TryGetValue("X-DeskBox-Token", out string? customVal) && !string.IsNullOrWhiteSpace(customVal))
        {
            candidate = customVal.Trim();
        }

        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] candidateBytes = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
    }

    private async Task RouteAuthenticatedRequestAsync(
        NetworkStream stream,
        ParsedHttpRequest req,
        CancellationToken ct)
    {
        string path = req.Path;
        string method = req.Method.ToUpperInvariant();

        // 1. MCP Streamable HTTP endpoint (/mcp)
        if (string.Equals(path, "/mcp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/mcp/", StringComparison.OrdinalIgnoreCase))
        {
            if (method == "GET")
            {
                await WriteResponseAsync(
                    stream,
                    statusCode: 405,
                    statusText: "Method Not Allowed",
                    contentType: "application/json; charset=utf-8",
                    body: """{"jsonrpc":"2.0","id":null,"error":{"code":-32000,"message":"Standalone GET SSE stream not required; send JSON-RPC via POST /mcp."}}"""u8.ToArray(),
                    extraHeader: "Allow: POST, DELETE, OPTIONS",
                    ct);
                return;
            }

            if (method == "DELETE")
            {
                await WriteResponseAsync(
                    stream,
                    statusCode: 200,
                    statusText: "OK",
                    contentType: "application/json; charset=utf-8",
                    body: """{"ok":true}"""u8.ToArray(),
                    extraHeader: null,
                    ct);
                return;
            }

            if (method != "POST")
            {
                await WriteJsonStatusAsync(stream, 405, "Method Not Allowed", """{"ok":false,"error":"Method not allowed."}""", ct);
                return;
            }

            await HandleMcpStreamableHttpPostAsync(stream, req.BodyUtf8, ct);
            return;
        }

        // 2. REST / Webhook API (/api/v1/...)
        if (method == "GET" &&
            (string.Equals(path, "/", StringComparison.Ordinal) ||
             string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(path, "/api/v1/health", StringComparison.OrdinalIgnoreCase)))
        {
            string healthJson = DeskBoxAgentBridgeServer.BuildSuccessJson(writer =>
            {
                writer.WriteString("service", "deskbox-agent-http-bridge");
                writer.WriteString("status", "online");
                writer.WriteNumber("port", ActualPort);
                writer.WriteString("mcpUrl", McpEndpointUrl);
                writer.WriteString("pushUrl", RestPushUrl);
                writer.WriteNumber("toolsCount", DeskBoxAgentCliEntry.GetMcpToolSummaries().Count);
                writer.WriteString("widgetsRoot", CustomWidgetDataStore.GetWidgetsRootDirectory());
            });
            await WriteJsonStatusAsync(stream, 200, "OK", healthJson, ct);
            return;
        }

        if (method == "GET" && string.Equals(path, "/api/v1/spec", StringComparison.OrdinalIgnoreCase))
        {
            byte[] md = Encoding.UTF8.GetBytes(CustomWidgetTemplates.AiAgentSkillMarkdown);
            await WriteResponseAsync(stream, 200, "OK", "text/markdown; charset=utf-8", md, null, ct);
            return;
        }

        if (method == "GET" && string.Equals(path, "/api/v1/schema", StringComparison.OrdinalIgnoreCase))
        {
            byte[] schema = Encoding.UTF8.GetBytes(CustomWidgetTemplates.ManifestJsonSchema);
            await WriteResponseAsync(stream, 200, "OK", "application/schema+json; charset=utf-8", schema, null, ct);
            return;
        }

        if (method == "GET" && string.Equals(path, "/api/v1/widgets", StringComparison.OrdinalIgnoreCase))
        {
            string resp = await DispatchBridgeOpAsync(w => w.WriteString("op", "list"), ct);
            await WriteJsonStatusAsync(stream, IsBridgeResponseOk(resp) ? 200 : 400, "OK", resp, ct);
            return;
        }

        if (method == "GET" && string.Equals(path, "/api/v1/widgets/get", StringComparison.OrdinalIgnoreCase))
        {
            string id = GetQueryParam(req.Query, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                await WriteJsonStatusAsync(stream, 400, "Bad Request", DeskBoxAgentBridgeServer.BuildErrorJson("Missing query parameter 'id'."), ct);
                return;
            }

            string resp = await DispatchBridgeOpAsync(w =>
            {
                w.WriteString("op", "get");
                w.WriteString("widgetId", id);
            }, ct);
            await WriteJsonStatusAsync(stream, IsBridgeResponseOk(resp) ? 200 : 404, IsBridgeResponseOk(resp) ? "OK" : "Not Found", resp, ct);
            return;
        }

        if (method == "GET" && string.Equals(path, "/api/v1/widgets/preview", StringComparison.OrdinalIgnoreCase))
        {
            string id = GetQueryParam(req.Query, "id");
            string theme = GetQueryParam(req.Query, "theme");
            string mode = GetQueryParam(req.Query, "mode");
            string format = GetQueryParam(req.Query, "format");
            if (string.IsNullOrWhiteSpace(id))
            {
                await WriteJsonStatusAsync(stream, 400, "Bad Request", DeskBoxAgentBridgeServer.BuildErrorJson("Missing query parameter 'id'."), ct);
                return;
            }

            string resp = await DispatchBridgeOpAsync(w =>
            {
                w.WriteString("op", "capture_preview");
                w.WriteString("widgetId", id);
                if (!string.IsNullOrWhiteSpace(theme))
                {
                    w.WriteString("theme", theme);
                }
                if (!string.IsNullOrWhiteSpace(mode))
                {
                    w.WriteString("mode", mode);
                }
            }, ct);

            if (!IsBridgeResponseOk(resp))
            {
                await WriteJsonStatusAsync(stream, 400, "Bad Request", resp, ct);
                return;
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonStatusAsync(stream, 200, "OK", resp, ct);
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(resp);
            string base64 = doc.RootElement.GetProperty("result").GetProperty("base64Png").GetString() ?? string.Empty;
            byte[] pngBytes = Convert.FromBase64String(base64);
            await WriteResponseAsync(stream, 200, "OK", "image/png", pngBytes, null, ct);
            return;
        }

        if (method == "POST" && string.Equals(path, "/api/v1/widgets/validate", StringComparison.OrdinalIgnoreCase))
        {
            string manifestJson = UnwrapManifestJsonFromRequestBody(req.BodyUtf8);
            string resp = await DispatchBridgeOpAsync(w =>
            {
                w.WriteString("op", "validate");
                w.WriteString("manifestJson", manifestJson);
            }, ct);
            await WriteJsonStatusAsync(stream, 200, "OK", resp, ct);
            return;
        }

        if (method == "POST" && string.Equals(path, "/api/v1/widgets/upsert", StringComparison.OrdinalIgnoreCase))
        {
            (string? widgetId, string manifestJson, bool resizeToDefault) = ParseUpsertBody(req.BodyUtf8, GetQueryParam(req.Query, "id"));
            string resp = await DispatchBridgeOpAsync(w =>
            {
                w.WriteString("op", "upsert");
                if (!string.IsNullOrWhiteSpace(widgetId))
                {
                    w.WriteString("widgetId", widgetId);
                }
                w.WriteString("manifestJson", manifestJson);
                w.WriteBoolean("resizeToDefault", resizeToDefault);
                w.WriteBoolean("reveal", true);
            }, ct);
            await WriteJsonStatusAsync(stream, IsBridgeResponseOk(resp) ? 200 : 400, IsBridgeResponseOk(resp) ? "OK" : "Bad Request", resp, ct);
            return;
        }

        if (method == "POST" && string.Equals(path, "/api/v1/widgets/push", StringComparison.OrdinalIgnoreCase))
        {
            string resp = await ForwardPushBodyAsync(req.BodyUtf8, GetQueryParam(req.Query, "id"), ct);
            await WriteJsonStatusAsync(stream, IsBridgeResponseOk(resp) ? 200 : 400, IsBridgeResponseOk(resp) ? "OK" : "Bad Request", resp, ct);
            return;
        }

        await WriteJsonStatusAsync(
            stream,
            404,
            "Not Found",
            DeskBoxAgentBridgeServer.BuildErrorJson($"Unknown endpoint '{method} {path}'. Use POST /mcp or /api/v1/health."),
            ct);
    }

    private async Task HandleMcpStreamableHttpPostAsync(
        NetworkStream stream,
        string bodyUtf8,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bodyUtf8))
        {
            await WriteJsonStatusAsync(
                stream,
                400,
                "Bad Request",
                """{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Empty JSON-RPC body."}}""",
                ct);
            return;
        }

        async Task<string?> DirectSender(Action<Utf8JsonWriter> writePayload, CancellationToken token)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                writePayload(w);
                w.WriteEndObject();
            }
            return await _bridgeJsonDispatcher(Encoding.UTF8.GetString(ms.ToArray()), token);
        }

        string trimmed = bodyUtf8.TrimStart();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using JsonDocument batchDoc = JsonDocument.Parse(bodyUtf8);
                if (batchDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var responses = new List<string>();
                    foreach (JsonElement item in batchDoc.RootElement.EnumerateArray())
                    {
                        string? itemResp = await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync(
                            item.GetRawText(),
                            pipeNameOverride: null,
                            cancellationToken: ct,
                            directBridgeSender: DirectSender);
                        if (!string.IsNullOrEmpty(itemResp))
                        {
                            responses.Add(itemResp);
                        }
                    }

                    if (responses.Count == 0)
                    {
                        await WriteResponseAsync(stream, 202, "Accepted", "application/json; charset=utf-8", [], null, ct);
                        return;
                    }

                    string batchJson = "[" + string.Join(",", responses) + "]";
                    await WriteJsonStatusAsync(stream, 200, "OK", batchJson, ct);
                    return;
                }
            }
            catch (Exception ex)
            {
                string err = $"{{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{{\"code\":-32700,\"message\":\"Parse error: {JsonEncodedText.Encode(ex.Message)}\"}}}}";
                await WriteJsonStatusAsync(stream, 400, "Bad Request", err, ct);
                return;
            }
        }

        string? singleResp = await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync(
            bodyUtf8,
            pipeNameOverride: null,
            cancellationToken: ct,
            directBridgeSender: DirectSender);

        if (string.IsNullOrEmpty(singleResp))
        {
            // JSON-RPC notification (e.g. notifications/initialized) -> 202 Accepted per MCP Streamable HTTP spec
            await WriteResponseAsync(stream, 202, "Accepted", "application/json; charset=utf-8", [], null, ct);
            return;
        }

        await WriteJsonStatusAsync(stream, 200, "OK", singleResp, ct);
    }

    private async Task<string> DispatchBridgeOpAsync(Action<Utf8JsonWriter> writeOp, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writeOp(writer);
            writer.WriteEndObject();
        }
        return await _bridgeJsonDispatcher(Encoding.UTF8.GetString(ms.ToArray()), ct);
    }

    private static string UnwrapManifestJsonFromRequestBody(string bodyUtf8)
    {
        if (string.IsNullOrWhiteSpace(bodyUtf8))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(bodyUtf8);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("manifestJson", out JsonElement mj))
                {
                    return mj.ValueKind == JsonValueKind.String ? mj.GetString() ?? string.Empty : mj.GetRawText();
                }

                if (doc.RootElement.TryGetProperty("manifest", out JsonElement mObj) &&
                    mObj.ValueKind == JsonValueKind.Object)
                {
                    return mObj.GetRawText();
                }
            }
        }
        catch
        {
            // Let validator report exact syntax error on raw body
        }

        return bodyUtf8;
    }

    private static (string? WidgetId, string ManifestJson, bool ResizeToDefault) ParseUpsertBody(
        string bodyUtf8,
        string queryId)
    {
        string? widgetId = string.IsNullOrWhiteSpace(queryId) ? null : queryId;
        bool resize = false;
        string manifestJson = bodyUtf8;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(bodyUtf8);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("widgetId", out JsonElement wid) && wid.ValueKind == JsonValueKind.String)
                {
                    widgetId = wid.GetString();
                }

                if (root.TryGetProperty("resizeToDefault", out JsonElement rz) && rz.ValueKind == JsonValueKind.True)
                {
                    resize = true;
                }

                if (root.TryGetProperty("manifestJson", out JsonElement mj))
                {
                    manifestJson = mj.ValueKind == JsonValueKind.String
                        ? mj.GetString() ?? string.Empty
                        : mj.GetRawText();
                }
                else if (root.TryGetProperty("manifest", out JsonElement mObj) &&
                         mObj.ValueKind == JsonValueKind.Object)
                {
                    manifestJson = mObj.GetRawText();
                }
            }
        }
        catch
        {
        }

        return (widgetId, manifestJson, resize);
    }

    private async Task<string> ForwardPushBodyAsync(string bodyUtf8, string queryWidgetId, CancellationToken ct)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(bodyUtf8) ? "{}" : bodyUtf8);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return DeskBoxAgentBridgeServer.BuildErrorJson("Push body must be a JSON object.");
            }

            string widgetId = !string.IsNullOrWhiteSpace(queryWidgetId)
                ? queryWidgetId
                : (root.TryGetProperty("widgetId", out JsonElement wid) && wid.ValueKind == JsonValueKind.String
                    ? wid.GetString() ?? CustomWidgetTemplates.AgentHudTemplateId
                    : CustomWidgetTemplates.AgentHudTemplateId);

            return await DispatchBridgeOpAsync(w =>
            {
                w.WriteString("op", "push_data");
                w.WriteString("widgetId", widgetId);
                if (root.TryGetProperty("state", out JsonElement st) && st.ValueKind == JsonValueKind.Object)
                {
                    w.WritePropertyName("state");
                    st.WriteTo(w);
                }
                if (root.TryGetProperty("collection", out JsonElement col) && col.ValueKind == JsonValueKind.String)
                {
                    w.WriteString("collection", col.GetString());
                }
                if (root.TryGetProperty("mode", out JsonElement md) && md.ValueKind == JsonValueKind.String)
                {
                    w.WriteString("mode", md.GetString());
                }
                if (root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
                {
                    w.WritePropertyName("items");
                    items.WriteTo(w);
                }
            }, ct);
        }
        catch (Exception ex)
        {
            return DeskBoxAgentBridgeServer.BuildErrorJson($"Invalid JSON push payload: {ex.Message}");
        }
    }

    private static bool IsBridgeResponseOk(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string GetQueryParam(string rawQuery, string key)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return string.Empty;
        }

        string trimmed = rawQuery.TrimStart('?');
        foreach (string part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string k = Uri.UnescapeDataString(part[..eq]);
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            }
        }

        return string.Empty;
    }

    private static async Task<ParsedHttpRequest?> TryReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        string? requestLine = await ReadAsciiLineAsync(stream, ct);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return null;
        }

        string[] parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        string method = parts[0].Trim();
        string rawTarget = parts[1].Trim();
        int qIdx = rawTarget.IndexOf('?');
        string path = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;
        string query = qIdx >= 0 ? rawTarget[(qIdx + 1)..] : string.Empty;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < MaxHeaderCount; i++)
        {
            string? line = await ReadAsciiLineAsync(stream, ct);
            if (line is null || line.Length == 0)
            {
                break;
            }

            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                string name = line[..colon].Trim();
                string val = line[(colon + 1)..].Trim();
                headers[name] = val;
            }
        }

        string bodyUtf8 = string.Empty;
        if (headers.TryGetValue("Transfer-Encoding", out string? te) &&
            te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            byte[] chunked = await ReadChunkedBodyAsync(stream, ct);
            bodyUtf8 = Encoding.UTF8.GetString(chunked);
        }
        else if (headers.TryGetValue("Content-Length", out string? clRaw) &&
                 int.TryParse(clRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int contentLength) &&
                 contentLength > 0)
        {
            if (contentLength > MaxRequestBodyBytes)
            {
                throw new InvalidDataException("HTTP request body exceeds 2 MB limit.");
            }

            byte[] buffer = new byte[contentLength];
            int totalRead = 0;
            while (totalRead < contentLength)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, contentLength - totalRead), ct);
                if (read <= 0)
                {
                    break;
                }
                totalRead += read;
            }
            bodyUtf8 = Encoding.UTF8.GetString(buffer, 0, totalRead);
        }

        return new ParsedHttpRequest(method, path, query, headers, bodyUtf8);
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            string? sizeLine = await ReadAsciiLineAsync(stream, ct);
            if (sizeLine is null)
            {
                break;
            }

            int semi = sizeLine.IndexOf(';');
            string hex = (semi >= 0 ? sizeLine[..semi] : sizeLine).Trim();
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int chunkLen) || chunkLen < 0)
            {
                break;
            }

            if (chunkLen == 0)
            {
                await ReadAsciiLineAsync(stream, ct);
                break;
            }

            if (ms.Length + chunkLen > MaxRequestBodyBytes)
            {
                throw new InvalidDataException("Chunked HTTP body exceeds 2 MB limit.");
            }

            byte[] buf = new byte[chunkLen];
            int readTotal = 0;
            while (readTotal < chunkLen)
            {
                int r = await stream.ReadAsync(buf.AsMemory(readTotal, chunkLen - readTotal), ct);
                if (r <= 0)
                {
                    break;
                }
                readTotal += r;
            }
            ms.Write(buf, 0, readTotal);
            await ReadAsciiLineAsync(stream, ct);
        }

        return ms.ToArray();
    }

    private static async Task<string?> ReadAsciiLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var lineBytes = new List<byte>(128);
        byte[] one = new byte[1];
        while (lineBytes.Count < MaxHeaderLineBytes)
        {
            int read = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (read == 0)
            {
                return lineBytes.Count == 0 ? null : Encoding.UTF8.GetString(lineBytes.ToArray());
            }

            byte b = one[0];
            if (b == (byte)'\n')
            {
                if (lineBytes.Count > 0 && lineBytes[^1] == (byte)'\r')
                {
                    lineBytes.RemoveAt(lineBytes.Count - 1);
                }
                return Encoding.UTF8.GetString(lineBytes.ToArray());
            }

            lineBytes.Add(b);
        }

        throw new InvalidDataException("HTTP header line too long.");
    }

    private static Task WriteJsonStatusAsync(
        NetworkStream stream,
        int statusCode,
        string statusText,
        string json,
        CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        return WriteResponseAsync(stream, statusCode, statusText, "application/json; charset=utf-8", bytes, null, ct);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string statusText,
        string contentType,
        byte[] body,
        string? extraHeader,
        CancellationToken ct)
    {
        var sb = new StringBuilder(256);
        sb.Append("HTTP/1.1 ").Append(statusCode.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(statusText).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        if (!string.IsNullOrWhiteSpace(extraHeader))
        {
            sb.Append(extraHeader).Append("\r\n");
        }
        sb.Append("\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes, ct);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, ct);
        }
        await stream.FlushAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsRunning = false;
        ActualPort = 0;

        try
        {
            await _cts.CancelAsync();
        }
        catch
        {
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
        }
        _listener = null;

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }

        Task[] pending;
        lock (_gate)
        {
            pending = _clientTasks.ToArray();
            _clientTasks.Clear();
        }

        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }

        _cts.Dispose();
    }

    private sealed record ParsedHttpRequest(
        string Method,
        string Path,
        string Query,
        IReadOnlyDictionary<string, string> Headers,
        string BodyUtf8);
}
