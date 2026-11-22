using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Zero-dependency, NativeAOT-clean MCP stdio server (<c>DeskBox.exe --mcp</c>) and
/// developer/agent CLI (<c>DeskBox.exe widget &lt;subcommand&gt;</c>).
/// Runs before XAML initialization so startup takes &lt;15ms and ~12MB RAM.
/// </summary>
public static partial class DeskBoxAgentCliEntry
{
    public static bool TryIntercept(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0)
        {
            return false;
        }

        string first = args[0].Trim();
        if (string.Equals(first, "--mcp", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = RunMcpStdioLoopAsync().GetAwaiter().GetResult();
            return true;
        }

        if (string.Equals(first, "widget", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "--widget-cli", StringComparison.OrdinalIgnoreCase))
        {
            CustomWidgetStorageFileOps.TryEnsureCliConsoleAttached();
            TryConfigureUtf8Console();
            exitCode = RunWidgetCliAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
            return true;
        }

        return false;
    }

    private static void TryConfigureUtf8Console()
    {
        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = utf8NoBom;
            Console.OutputEncoding = utf8NoBom;
        }
        catch
        {
        }
    }

    public static async Task<int> RunMcpStdioLoopAsync(
        Stream? inputOverride = null,
        Stream? outputOverride = null,
        string? pipeNameOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (inputOverride is null && outputOverride is null)
        {
            TryConfigureUtf8Console();
        }

        Stream stdin = inputOverride ?? Console.OpenStandardInput();
        Stream stdout = outputOverride ?? Console.OpenStandardOutput();
        var utf8 = new UTF8Encoding(false);
        var reader = new BufferedMcpStdioReader(stdin);

        while (!cancellationToken.IsCancellationRequested)
        {
            (string? messageJson, bool framedWithContentLength) = await ReadNextRpcMessageAsync(reader, cancellationToken);
            if (messageJson is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(messageJson))
            {
                continue;
            }

            string? responseJson;
            try
            {
                responseJson = await HandleMcpRpcMessageAsync(messageJson, pipeNameOverride, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                responseJson = BuildJsonRpcError(null, -32603, $"Internal error: {ex.Message}");
            }
            if (string.IsNullOrEmpty(responseJson))
            {
                continue;
            }

            byte[] payloadBytes = utf8.GetBytes(responseJson);
            if (framedWithContentLength)
            {
                byte[] headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {payloadBytes.Length}\r\n\r\n");
                await stdout.WriteAsync(headerBytes, cancellationToken);
                await stdout.WriteAsync(payloadBytes, cancellationToken);
            }
            else
            {
                await stdout.WriteAsync(payloadBytes, cancellationToken);
                await stdout.WriteAsync("\n"u8.ToArray(), cancellationToken);
            }

            await stdout.FlushAsync(cancellationToken);
        }

        return 0;
    }

    private sealed class BufferedMcpStdioReader(Stream stream)
    {
        private const int MaxStdioPayloadBytes = 16 * 1024 * 1024;
        private readonly byte[] _buffer = new byte[32 * 1024];
        private int _pos;
        private int _len;

        public async Task<string?> ReadLineUtf8Async(CancellationToken cancellationToken)
        {
            using var lineBytes = new MemoryStream(256);
            while (true)
            {
                if (_pos >= _len)
                {
                    _pos = 0;
                    _len = await stream.ReadAsync(_buffer.AsMemory(), cancellationToken);
                    if (_len <= 0)
                    {
                        if (lineBytes.Length == 0)
                        {
                            return null;
                        }

                        break;
                    }
                }

                int newlineIndex = Array.IndexOf(_buffer, (byte)'\n', _pos, _len - _pos);
                if (newlineIndex >= 0)
                {
                    int sliceLen = newlineIndex - _pos;
                    if (lineBytes.Length + sliceLen > MaxStdioPayloadBytes)
                    {
                        throw new InvalidDataException("MCP stdio line exceeds the 16 MB limit.");
                    }

                    if (sliceLen > 0)
                    {
                        lineBytes.Write(_buffer, _pos, sliceLen);
                    }

                    _pos = newlineIndex + 1;
                    break;
                }

                int chunkLen = _len - _pos;
                if (lineBytes.Length + chunkLen > MaxStdioPayloadBytes)
                {
                    throw new InvalidDataException("MCP stdio line exceeds the 16 MB limit.");
                }

                lineBytes.Write(_buffer, _pos, chunkLen);
                _pos = _len;
            }

            byte[] raw = lineBytes.GetBuffer();
            int length = (int)lineBytes.Length;
            if (length > 0 && raw[length - 1] == (byte)'\r')
            {
                length--;
            }

            return length == 0 ? string.Empty : Encoding.UTF8.GetString(raw, 0, length);
        }

        public async Task<string> ReadExactUtf8BytesAsync(int byteLength, CancellationToken cancellationToken)
        {
            if (byteLength <= 0)
            {
                return string.Empty;
            }

            if (byteLength > MaxStdioPayloadBytes)
            {
                throw new InvalidDataException("MCP Content-Length exceeds the 16 MB limit.");
            }

            byte[] payload = new byte[byteLength];
            int totalRead = 0;
            while (totalRead < byteLength)
            {
                int available = _len - _pos;
                if (available > 0)
                {
                    int toCopy = Math.Min(available, byteLength - totalRead);
                    Buffer.BlockCopy(_buffer, _pos, payload, totalRead, toCopy);
                    _pos += toCopy;
                    totalRead += toCopy;
                    continue;
                }

                int read = await stream.ReadAsync(payload.AsMemory(totalRead, byteLength - totalRead), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
            }

            return totalRead == 0
                ? string.Empty
                : Encoding.UTF8.GetString(payload, 0, totalRead).TrimStart('\uFEFF');
        }
    }

    private static async Task<(string? Json, bool IsContentLengthFramed)> ReadNextRpcMessageAsync(
        BufferedMcpStdioReader reader,
        CancellationToken cancellationToken)
    {
        string? line = await reader.ReadLineUtf8Async(cancellationToken);
        if (line is null)
        {
            return (null, false);
        }

        if ((line = line.TrimStart('\uFEFF')).StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
        {
            string lenStr = line["Content-Length:".Length..].Trim();
            if (!int.TryParse(lenStr, out int byteLength) || byteLength < 0 || byteLength > 16 * 1024 * 1024)
            {
                return (string.Empty, true);
            }

            while (true)
            {
                string? headerLine = await reader.ReadLineUtf8Async(cancellationToken);
                if (headerLine is null || headerLine.Length == 0)
                {
                    break;
                }
            }

            string payloadJson = await reader.ReadExactUtf8BytesAsync(byteLength, cancellationToken);
            return (payloadJson, true);
        }

        return (line, false);
    }

    public sealed record McpToolSummary(string Name, string DescriptionKey, string FallbackDescription);

    public static IReadOnlyList<McpToolSummary> GetMcpToolSummaries() =>
    [
        new("deskbox_get_widget_spec", "CustomWidget.AgentHub.Tool.GetSpec", "Read SKILL.md specification & JSON Schema"),
        new("deskbox_get_capability_spec", "CustomWidget.AgentHub.Tool.GetCapabilitySpec", "Search or inspect atomic capability contracts & API Reference"),
        new("deskbox_get_studio_context", "CustomWidget.AgentHub.Tool.GetStudioContext", "Read live Widget Workshop context, selected node & draft manifest"),
        new("deskbox_list_widgets", "CustomWidget.AgentHub.Tool.ListWidgets", "List live desktop widgets & workspace directories"),
        new("deskbox_validate_manifest", "CustomWidget.AgentHub.Tool.Validate", "Validate manifest.json syntax, nodes & bindings"),
        new("deskbox_upsert_widget", "CustomWidget.AgentHub.Tool.Upsert", "Validate and stage a widget (desktop unchanged); commit only after user confirmation"),
        new("deskbox_capture_widget_preview", "CustomWidget.AgentHub.Tool.Capture", "Capture real WinUI 3 PNG screenshot (Dark/Light)"),
        new("deskbox_push_widget_data", "CustomWidget.AgentHub.Tool.PushData", "Push real-time state & events to desktop widgets")
    ];

    public static async Task<string?> HandleMcpRpcMessageAsync(
        string requestJson,
        string? pipeNameOverride = null,
        CancellationToken cancellationToken = default,
        Func<Action<Utf8JsonWriter>, CancellationToken, Task<string?>>? directBridgeSender = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(requestJson.TrimStart('\uFEFF'));
        }
        catch (Exception ex)
        {
            return BuildJsonRpcError(null, -32700, $"Parse error: {ex.Message}");
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                {
                    return BuildJsonRpcError(null, -32600, "Invalid Request: empty batch array.");
                }

                var batchResponses = new List<string>(root.GetArrayLength());
                foreach (JsonElement item in root.EnumerateArray())
                {
                    string? singleResp = await HandleSingleMcpRpcElementAsync(
                        item,
                        pipeNameOverride,
                        cancellationToken,
                        directBridgeSender);
                    if (!string.IsNullOrEmpty(singleResp))
                    {
                        batchResponses.Add(singleResp);
                    }
                }

                return batchResponses.Count == 0 ? null : "[" + string.Join(",", batchResponses) + "]";
            }

            return await HandleSingleMcpRpcElementAsync(
                root,
                pipeNameOverride,
                cancellationToken,
                directBridgeSender);
        }
    }

    private static string NegotiateMcpProtocolVersion(string? requestedVersion) =>
        requestedVersion switch
        {
            "2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25" => requestedVersion,
            _ => "2025-06-18"
        };

    private static async Task<string?> HandleSingleMcpRpcElementAsync(
        JsonElement root,
        string? pipeNameOverride,
        CancellationToken cancellationToken,
        Func<Action<Utf8JsonWriter>, CancellationToken, Task<string?>>? directBridgeSender)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return BuildJsonRpcError(null, -32600, "Invalid Request: expected JSON-RPC object.");
        }

        bool hasId = root.TryGetProperty("id", out JsonElement idEl) &&
                     idEl.ValueKind is not JsonValueKind.Undefined;
        if (!root.TryGetProperty("method", out JsonElement mEl) || mEl.ValueKind != JsonValueKind.String)
        {
            return BuildJsonRpcError(hasId ? idEl : null, -32600, "Invalid Request: missing string 'method'.");
        }

        string method = mEl.GetString() ?? string.Empty;
        if (!hasId)
        {
            // JSON-RPC Notification (e.g. notifications/initialized) -> no response
            return null;
        }

        JsonElement? paramsEl = root.TryGetProperty("params", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.Object
            ? pEl
            : null;

        return method switch
        {
            "initialize" => await HandleMcpInitializeAsync(
                idEl,
                paramsEl,
                pipeNameOverride,
                cancellationToken,
                directBridgeSender),

            "ping" => BuildJsonRpcResult(idEl, _ => { }),

            "resources/list" => BuildJsonRpcResult(idEl, writer =>
            {
                writer.WriteStartArray("resources");
                writer.WriteEndArray();
            }),

            "prompts/list" => BuildJsonRpcResult(idEl, writer =>
            {
                writer.WriteStartArray("prompts");
                writer.WriteEndArray();
            }),

            "tools/list" => BuildJsonRpcResult(idEl, WriteMcpToolsCatalog),

            "tools/call" => await HandleMcpToolCallAsync(idEl, paramsEl, pipeNameOverride, cancellationToken, directBridgeSender),

            _ => BuildJsonRpcError(idEl, -32601, $"Method not found: {method}")
        };
    }

    private static async Task<string> HandleMcpInitializeAsync(
        JsonElement idEl,
        JsonElement? paramsEl,
        string? pipeNameOverride,
        CancellationToken cancellationToken,
        Func<Action<Utf8JsonWriter>, CancellationToken, Task<string?>>? directBridgeSender)
    {
        if (paramsEl is { } p &&
            p.TryGetProperty("clientInfo", out JsonElement ciEl) &&
            ciEl.ValueKind == JsonValueKind.Object &&
            ciEl.TryGetProperty("name", out JsonElement cnEl) &&
            cnEl.ValueKind == JsonValueKind.String)
        {
            string? clientName = cnEl.GetString()?.Trim();
            string? clientVersion = ciEl.TryGetProperty("version", out JsonElement cvEl) && cvEl.ValueKind == JsonValueKind.String
                ? cvEl.GetString()?.Trim()
                : null;
            if (!string.IsNullOrWhiteSpace(clientName))
            {
                try
                {
                    Action<Utf8JsonWriter> writeHello = w =>
                    {
                        w.WriteString("op", "mcp_hello");
                        w.WriteString("clientName", clientName);
                        if (!string.IsNullOrWhiteSpace(clientVersion))
                        {
                            w.WriteString("clientVersion", clientVersion);
                        }
                    };

                    if (directBridgeSender is not null)
                    {
                        _ = await directBridgeSender(writeHello, cancellationToken);
                    }
                    else
                    {
                        _ = await DeskBoxAgentBridgeClient.TrySendAsync(
                            writeHello,
                            pipeNameOverride,
                            connectTimeoutMs: 180,
                            cancellationToken: cancellationToken);
                    }
                }
                catch
                {
                    // Handshake reporting is best-effort and must never fail initialize.
                }
            }
        }

        return BuildJsonRpcResult(idEl, writer =>
        {
            string? requestedVersion = paramsEl is { } pInit &&
                                       pInit.TryGetProperty("protocolVersion", out JsonElement pv) &&
                                       pv.ValueKind == JsonValueKind.String
                ? pv.GetString()
                : null;
            writer.WriteString("protocolVersion", NegotiateMcpProtocolVersion(requestedVersion));
            writer.WriteString("instructions", AgentSkillPackage.McpInstructions);
            writer.WriteStartObject("capabilities");
            writer.WriteStartObject("tools");
            writer.WriteBoolean("listChanged", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartObject("serverInfo");
            writer.WriteString("name", "deskbox");
            writer.WriteString("version", CustomWidgetAgentConnectorService.GetAppVersionString());
            writer.WriteEndObject();
        });
    }

    private static void WriteToolAnnotations(
        Utf8JsonWriter writer,
        bool readOnlyHint,
        bool destructiveHint,
        bool idempotentHint,
        bool openWorldHint = false)
    {
        // NOTE: Use WritePropertyName + WriteStartObject (never WriteStartObject("annotations"))
        // so contract regexes scanning inputSchema property objects do not match tool annotations.
        writer.WritePropertyName("annotations");
        writer.WriteStartObject();
        writer.WriteBoolean("readOnlyHint", readOnlyHint);
        writer.WriteBoolean("destructiveHint", destructiveHint);
        writer.WriteBoolean("idempotentHint", idempotentHint);
        writer.WriteBoolean("openWorldHint", openWorldHint);
        writer.WriteEndObject();
    }

    private static void WriteMcpToolsCatalog(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("tools");

        // 1. deskbox_get_widget_spec
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_get_widget_spec");
        writer.WriteString("description", "Get DeskBox's native WinUI 3 Custom Widget developer specification (SKILL.md), Tier-0 capability directory, and optional JSON Schema. Defaults to detail='compact' (~2 KB) for token-efficient progressive disclosure with deskbox_get_capability_spec; pass detail='full' for the complete SKILL.md.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteStartObject("properties");
        writer.WriteStartObject("detail");
        writer.WriteString("type", "string");
        writer.WriteStartArray("enum");
        writer.WriteStringValue("compact");
        writer.WriteStringValue("full");
        writer.WriteEndArray();
        writer.WriteString("description", "Optional verbosity: 'compact' (default) returns Tier-0 capability & component directory + capabilityId slugs (~2 KB); 'full' includes the complete SKILL.md.");
        writer.WriteEndObject();
        writer.WriteStartObject("includeSchema");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "Include full Draft 2020-12 JSON Schema in addition to the specification.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        WriteToolAnnotations(writer, readOnlyHint: true, destructiveHint: false, idempotentHint: true);
        writer.WriteEndObject();

        // 1b. deskbox_get_capability_spec
        IReadOnlyList<CustomWidgetCapabilitySpec> allCapabilities = CustomWidgetCapabilityRegistry.All;
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_get_capability_spec");
        writer.WriteString("description", $"Query DeskBox's Single-Source-of-Truth Capability Registry ({allCapabilities.Count} atomic capsules across weather, calendar/lunar, systemMetrics, deviceState/system.control, file.transform/DropZone, secret vault + authenticated httpJson headers, aiMonitor, countdown, pomodoro, charts, script, etc.). Pass 'capabilityId' or 'query' to get exact tokens, properties, auto-linked dependencies, and Golden JSON snippets, or omit both to list all capabilities.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteStartObject("properties");
        writer.WriteStartObject("capabilityId");
        writer.WriteString("type", "string");
        writer.WriteStartArray("enum");
        foreach (CustomWidgetCapabilitySpec cap in allCapabilities)
        {
            writer.WriteStringValue(cap.Id);
        }
        writer.WriteEndArray();
        writer.WriteString("description", "Exact capability ID (e.g. 'weather', 'calendar', 'calendar-grid', 'system-control', 'file-transform', 'drop-zone', 'secret-vault', 'http-and-rss-feeds', 'ai-monitor'). Automatically expands required dependencies.");
        writer.WriteEndObject();
        writer.WriteStartObject("query");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Keyword search across capability titles, $namespace.* tokens, node types, and action types (e.g. 'weather', 'lunar', 'volume', 'png', 'secret').");
        writer.WriteEndObject();
        writer.WriteStartObject("includeFullReference");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "When true, returns the complete API_REFERENCE.md Markdown document.");
        writer.WriteEndObject();
        writer.WriteStartObject("language");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Optional culture code (e.g. 'zh-CN' or 'en-US') controlling reference language.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        WriteToolAnnotations(writer, readOnlyHint: true, destructiveHint: false, idempotentHint: true);
        writer.WriteEndObject();

        // 1c. deskbox_get_studio_context
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_get_studio_context");
        writer.WriteString("description", "Read live Widget Workshop (Studio) context when collaborating with the user: whether Workshop is open, active widgetId/name, user-selected target node path, capsule vs expanded viewport focus, pinned capabilities, design brief, active draft manifest.json, and pending stageIds.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteStartObject("properties");
        writer.WriteStartObject("includeManifest");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "Defaults to true. Set false to inspect Workshop metadata, selectedNode, and pendingStages without the full draft manifestJson.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        WriteToolAnnotations(writer, readOnlyHint: true, destructiveHint: false, idempotentHint: true);
        writer.WriteEndObject();

        // 2. deskbox_list_widgets
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_list_widgets");
        writer.WriteString("description", "List all Custom Widgets in DeskBox (widgetId, name, icon, workspace directory, manifest.json path), or pass 'widgetId' to inspect one widget's full manifest.json and live runtime state (including sourceStatus/sourceError, displayMode, collectionCounts, and sampleItems).");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteStartObject("properties");
        writer.WriteStartObject("widgetId");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Optional widgetId or manifest id. When provided, inspects that widget's manifest.json and live runtime state/collections.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        WriteToolAnnotations(writer, readOnlyHint: true, destructiveHint: false, idempotentHint: true);
        writer.WriteEndObject();

        // 3. deskbox_validate_manifest
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_validate_manifest");
        writer.WriteString("description", "Validate a DeskBox Custom Widget manifest.json string, checking JSON syntax, supported WinUI components/actions, and undeclared state/settings/collections references.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteStartArray("required");
        writer.WriteStringValue("manifestJson");
        writer.WriteEndArray();
        writer.WriteStartObject("properties");
        writer.WriteStartObject("manifestJson");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Full manifest.json content to validate.");
        writer.WriteEndObject();
        writer.WriteStartObject("logicJs");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Optional ES2023 logic.js sidecar module to compile and dry-run in QuickJS along with manifestJson.");
        writer.WriteEndObject();
        writer.WriteStartObject("locales");
        writer.WriteString("type", "object");
        writer.WriteString("description", "Optional per-locale dictionary map { 'en-US': { 'key': 'value' }, 'zh-CN': { ... } } to validate.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        WriteToolAnnotations(writer, readOnlyHint: true, destructiveHint: false, idempotentHint: true);
        writer.WriteEndObject();

        // 4. deskbox_upsert_widget
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_upsert_widget");
        writer.WriteString("description",
            "Validate and STAGE a DeskBox Custom Widget. By default (commit=false) this requires manifestJson, creates a pending stage (desktop unchanged), and returns a stageId. " +
            "Ask the user to confirm, then call again with commit=true and the same stageId (manifestJson is optional on commit; when omitted, the staged bundle is committed directly). " +
            "Pass an explicit widgetId when updating an existing widget.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteStartObject("properties");
        writer.WriteStartObject("widgetId");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Optional existing desktop widgetId instance to update. Omit when creating a brand-new widget.");
        writer.WriteEndObject();
        writer.WriteStartObject("manifestJson");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Valid canonical schemaVersion 1 Flat Element Graph manifest.json ('root' + 'elements' + 'popovers[*].root', semantic icon names). Required when creating or staging without incremental edits; optional when patching an existing widget with upsertElements/removeElements/patchRoot/removeRootKeys/scriptHunks or committing an existing stageId.");
        writer.WriteEndObject();
        writer.WriteStartObject("upsertElements");
        writer.WriteString("type", "object");
        writer.WriteString("description", "Optional canonical schemaVersion 1 Flat Element Graph dictionary of elementId -> node object to create or replace in root 'elements'.");
        writer.WriteEndObject();
        writer.WriteStartObject("removeElements");
        writer.WriteString("type", "array");
        writer.WriteString("description", "Optional list of element IDs to remove from root 'elements' (automatically strips dangling child ID references).");
        writer.WriteStartObject("items");
        writer.WriteString("type", "string");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartObject("patchRoot");
        writer.WriteString("type", "object");
        writer.WriteString("description", "Optional top-level manifest keys to merge/replace (e.g. name, state, collections, sources, popovers, capsule, settings, defaultSize, root).");
        writer.WriteEndObject();
        writer.WriteStartObject("removeRootKeys");
        writer.WriteString("type", "array");
        writer.WriteString("description", "Optional top-level manifest keys to delete (e.g. popovers, sources, onDrop).");
        writer.WriteStartObject("items");
        writer.WriteString("type", "string");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartObject("scriptHunks");
        writer.WriteString("type", "array");
        writer.WriteString("description", "Optional SEARCH/REPLACE hunks [{search, replace}] applied incrementally to existing logic.js.");
        writer.WriteStartObject("items");
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        writer.WriteStartObject("search");
        writer.WriteString("type", "string");
        writer.WriteEndObject();
        writer.WriteStartObject("replace");
        writer.WriteString("type", "string");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartArray("required");
        writer.WriteStringValue("search");
        writer.WriteStringValue("replace");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartObject("resizeToDefault");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "Resize existing widget window to manifest defaultSize.");
        writer.WriteEndObject();
        writer.WriteStartObject("logicJs");
        writer.WriteStartArray("type");
        writer.WriteStringValue("string");
        writer.WriteStringValue("null");
        writer.WriteEndArray();
        writer.WriteString("description", "Optional logic.js sidecar script (omit to keep existing, pass string to create/replace, pass null to remove).");
        writer.WriteEndObject();
        writer.WriteStartObject("locales");
        writer.WriteStartArray("type");
        writer.WriteStringValue("object");
        writer.WriteStringValue("null");
        writer.WriteEndArray();
        writer.WriteString("description", "Optional per-locale dictionary map { 'en-US': { 'key': 'value' } } written to locales/<culture>.json (omit to keep existing, pass object to create/replace, pass null to remove).");
        writer.WriteEndObject();
        writer.WriteStartObject("stageId");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Required when commit=true: the stageId returned by the preceding staged call. Reusing a committed stageId is idempotent.");
        writer.WriteEndObject();
        writer.WriteStartObject("commit");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "Defaults to false (stage only, desktop unchanged). Set true only after the user explicitly confirms the staged preview; requires stageId (manifestJson optional).");
        writer.WriteEndObject();
        writer.WriteStartObject("confirmDataLoss");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "Set true together with commit=true only after the user explicitly confirms dropping existing store collections or non-empty state keys reported in dataSafetyRisk.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        // Overwrites widgets and can clear their state: a destructive tool. MCP clients
        // surface their own confirmation for destructive tools; the host-side approval
        // gate in DeskBoxAgentBridgeServer remains the actual enforcement.
        WriteToolAnnotations(writer, readOnlyHint: false, destructiveHint: true, idempotentHint: true);
        writer.WriteEndObject();

        // 5. deskbox_capture_widget_preview
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_capture_widget_preview");
        writer.WriteString("description", "Capture a real native WinUI 3 PNG screenshot of a live DeskBox Custom Widget or a pending staged bundle so you can visually inspect layout, typography, spacing, Light/Dark theme contrast, and measured overflow. Defaults to 'expanded' mode.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteStartObject("properties");
        writer.WriteStartObject("widgetId");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Target widgetId returned by deskbox_list_widgets or deskbox_upsert_widget (optional if stageId is supplied).");
        writer.WriteEndObject();
        writer.WriteStartObject("stageId");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Optional pending stageId returned by deskbox_upsert_widget to capture a staged preview before committing.");
        writer.WriteEndObject();
        writer.WriteStartObject("theme");
        writer.WriteString("type", "string");
        writer.WriteStartArray("enum");
        writer.WriteStringValue("dark");
        writer.WriteStringValue("light");
        writer.WriteEndArray();
        writer.WriteString("description", "Optional theme override ('dark' or 'light').");
        writer.WriteEndObject();
        writer.WriteStartObject("mode");
        writer.WriteString("type", "string");
        writer.WriteStartArray("enum");
        writer.WriteStringValue("expanded");
        writer.WriteStringValue("capsule");
        writer.WriteStringValue("live");
        writer.WriteEndArray();
        writer.WriteString("description", "Optional capture mode: 'expanded' (default, renders full widget body even in Capsule Mode), 'capsule' (renders compact capsule strip), or 'live' (captures raw desktop window as-is).");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        WriteToolAnnotations(writer, readOnlyHint: true, destructiveHint: false, idempotentHint: true);
        writer.WriteEndObject();

        // 6. deskbox_push_widget_data
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_push_widget_data");
        writer.WriteString("description", "Push live reactive state key-values or collection records to a DeskBox Custom Widget (e.g. 'agent-hud' Ambient Agent HUD) in real time.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteStartObject("properties");
        writer.WriteStartObject("widgetId");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Target widgetId or 'agent-hud' (auto-creates the Agent HUD widget if needed).");
        writer.WriteEndObject();
        writer.WriteStartObject("state");
        writer.WriteString("type", "object");
        writer.WriteString("description", "Key-value state updates (e.g. agentName, taskTitle, statusText, progress, isBusy, updatedAt).");
        writer.WriteEndObject();
        writer.WriteStartObject("collection");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Optional collection name (e.g. 'events').");
        writer.WriteEndObject();
        writer.WriteStartObject("mode");
        writer.WriteString("type", "string");
        writer.WriteStartArray("enum");
        writer.WriteStringValue("prepend");
        writer.WriteStringValue("append");
        writer.WriteStringValue("replace");
        writer.WriteStringValue("upsert");
        writer.WriteStringValue("clear");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteStartObject("items");
        writer.WriteString("type", "array");
        writer.WriteStartObject("items");
        writer.WriteString("type", "object");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        WriteToolAnnotations(writer, readOnlyHint: false, destructiveHint: false, idempotentHint: false);
        writer.WriteEndObject();

        writer.WriteEndArray();
    }

    private static async Task<string> HandleMcpToolCallAsync(
        JsonElement idEl,
        JsonElement? paramsEl,
        string? pipeNameOverride,
        CancellationToken cancellationToken,
        Func<Action<Utf8JsonWriter>, CancellationToken, Task<string?>>? directBridgeSender = null)
    {
        if (paramsEl is not { } p ||
            !p.TryGetProperty("name", out JsonElement nameEl) ||
            nameEl.ValueKind != JsonValueKind.String)
        {
            return BuildJsonRpcError(idEl, -32602, "Missing tool name.");
        }

        string toolName = nameEl.GetString() ?? string.Empty;
        JsonElement argsEl = p.TryGetProperty("arguments", out JsonElement aEl) && aEl.ValueKind == JsonValueKind.Object
            ? aEl
            : default;

        switch (toolName)
        {
            case "deskbox_get_widget_spec":
            {
                bool includeSchema = argsEl.ValueKind == JsonValueKind.Object &&
                                     argsEl.TryGetProperty("includeSchema", out JsonElement incEl) &&
                                     incEl.ValueKind == JsonValueKind.True;
                string detail = argsEl.ValueKind == JsonValueKind.Object &&
                                argsEl.TryGetProperty("detail", out JsonElement detEl) &&
                                detEl.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(detEl.GetString())
                    ? detEl.GetString()!.Trim()
                    : "compact";
                bool isCompact = !string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase);

                var sb = new StringBuilder();
                sb.AppendLine($"DeskBox Version: {CustomWidgetAgentConnectorService.GetAppVersionString()} (Manifest Schema v1)");
                sb.AppendLine($"Widgets Workspace Root: {CustomWidgetDataStore.GetWidgetsRootDirectory()}");
                sb.AppendLine($"Active DeskBox UI Locale: {LocalizationService.ResolvePersistedOrAmbientCultureName()}");
                sb.AppendLine();
                sb.AppendLine(CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock());
                sb.AppendLine();
                if (isCompact)
                {
                    sb.Append("- Available `capabilityId` slugs (")
                      .Append(CustomWidgetCapabilityRegistry.All.Count)
                      .Append("): ")
                      .AppendLine(string.Join(", ", CustomWidgetCapabilityRegistry.All.Select(static c => $"`{c.Id}`")));
                    sb.AppendLine();
                    sb.AppendLine("## Stage-by-Default & On-Demand Capabilities");
                    sb.AppendLine("- Call `deskbox_get_capability_spec` (`capabilityId` or `query`) for Golden JSON snippets of any capability above, or call `deskbox_get_widget_spec` with `detail: \"full\"` for the complete SKILL.md.");
                    sb.AppendLine("- Call `deskbox_get_studio_context` to inspect any open Widget Workshop draft, selected node path, or pending stages.");
                    sb.AppendLine("- Validate via `deskbox_validate_manifest`, then call `deskbox_upsert_widget` (STAGE-BY-DEFAULT: returns `stageId`, desktop unchanged). Only pass `commit: true` with the same `stageId` (`manifestJson` optional) after explicit user confirmation.");
                }
                else
                {
                    sb.AppendLine(CustomWidgetTemplates.AiAgentSkillMarkdown);
                }

                if (includeSchema)
                {
                    sb.AppendLine();
                    sb.AppendLine("## manifest.schema.json");
                    sb.AppendLine("```json");
                    sb.AppendLine(CustomWidgetTemplates.ManifestJsonSchema);
                    sb.AppendLine("```");
                }
                return BuildMcpTextToolResult(idEl, sb.ToString(), isError: false);
            }

            case "deskbox_get_studio_context":
            {
                bool includeManifest = argsEl.ValueKind != JsonValueKind.Object ||
                                       !argsEl.TryGetProperty("includeManifest", out JsonElement incManifestEl) ||
                                       incManifestEl.ValueKind != JsonValueKind.False;
                string? bridgeResp = await SendToBridgeEnsuringAppAsync(
                    w =>
                    {
                        w.WriteString("op", "studio_context");
                        w.WriteBoolean("includeManifest", includeManifest);
                    },
                    pipeNameOverride,
                    autoLaunchAppIfOffline: false,
                    cancellationToken,
                    directBridgeSender);

                if (bridgeResp is not null)
                {
                    return BuildMcpBridgeResult(idEl, bridgeResp);
                }

                return BuildMcpTextToolResult(idEl, BuildOfflineStudioContextJson(), isError: false);
            }

            case "deskbox_get_capability_spec":
            {
                bool fullRef = argsEl.ValueKind == JsonValueKind.Object &&
                               argsEl.TryGetProperty("includeFullReference", out JsonElement fullEl) &&
                               fullEl.ValueKind == JsonValueKind.True;
                string? language = argsEl.ValueKind == JsonValueKind.Object &&
                                   argsEl.TryGetProperty("language", out JsonElement langEl) &&
                                   langEl.ValueKind == JsonValueKind.String
                    ? langEl.GetString()
                    : null;
                string? capId = argsEl.ValueKind == JsonValueKind.Object &&
                                argsEl.TryGetProperty("capabilityId", out JsonElement cidEl) &&
                                cidEl.ValueKind == JsonValueKind.String
                    ? cidEl.GetString()
                    : null;
                string? query = argsEl.ValueKind == JsonValueKind.Object &&
                                argsEl.TryGetProperty("query", out JsonElement qEl) &&
                                qEl.ValueKind == JsonValueKind.String
                    ? qEl.GetString()
                    : null;

                string specText = BuildCapabilitySpecText(capId, query, fullRef, language, out bool capError);
                return BuildMcpTextToolResult(idEl, specText, isError: capError);
            }

            case "deskbox_validate_manifest":
            {
                string manifestJson = argsEl.ValueKind == JsonValueKind.Object &&
                                      argsEl.TryGetProperty("manifestJson", out JsonElement mj) &&
                                      mj.ValueKind == JsonValueKind.String
                    ? mj.GetString() ?? string.Empty
                    : string.Empty;
                string? logicJs = argsEl.ValueKind == JsonValueKind.Object &&
                                  argsEl.TryGetProperty("logicJs", out JsonElement lj) &&
                                  lj.ValueKind == JsonValueKind.String
                    ? lj.GetString()
                    : null;
                CustomWidgetLocaleCatalog? locales = null;
                if (argsEl.ValueKind == JsonValueKind.Object &&
                    argsEl.TryGetProperty("locales", out JsonElement locEl))
                {
                    if (locEl.ValueKind == JsonValueKind.Object)
                    {
                        locales = CustomWidgetLocaleCatalog.ParseCatalogElement(locEl);
                    }
                    else if (locEl.ValueKind == JsonValueKind.String)
                    {
                        locales = CustomWidgetLocaleCatalog.ParseCatalogJson(locEl.GetString());
                    }
                }

                string reportJson = DeskBoxAgentBridgeServer.BuildValidationReportJson(
                    manifestJson,
                    logicJs,
                    locales,
                    extraDiagnostics: null,
                    indented: true,
                    out bool isValid);
                return BuildMcpTextToolResult(idEl, reportJson, isError: !isValid);
            }

            case "deskbox_list_widgets":
            {
                string? targetWidgetId = argsEl.ValueKind == JsonValueKind.Object &&
                                         argsEl.TryGetProperty("widgetId", out JsonElement widFilterEl) &&
                                         widFilterEl.ValueKind == JsonValueKind.String
                    ? widFilterEl.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(targetWidgetId))
                {
                    string trimmedId = targetWidgetId.Trim();
                    string? singleBridgeResp = await SendToBridgeEnsuringAppAsync(
                        w =>
                        {
                            w.WriteString("op", "get");
                            w.WriteString("widgetId", trimmedId);
                        },
                        pipeNameOverride,
                        autoLaunchAppIfOffline: false,
                        cancellationToken,
                        directBridgeSender);

                    if (singleBridgeResp is not null)
                    {
                        return BuildMcpBridgeResult(idEl, singleBridgeResp);
                    }

                    string? offlineSingle = await BuildOfflineDiskSingleWidgetJsonAsync(trimmedId);
                    return offlineSingle is not null
                        ? BuildMcpTextToolResult(idEl, offlineSingle, isError: false)
                        : BuildMcpTextToolResult(idEl, $"Custom widget '{trimmedId}' not found.", isError: true);
                }

                string? bridgeResp = await SendToBridgeEnsuringAppAsync(
                    w => w.WriteString("op", "list"),
                    pipeNameOverride,
                    autoLaunchAppIfOffline: false,
                    cancellationToken,
                    directBridgeSender);

                if (bridgeResp is not null)
                {
                    return BuildMcpBridgeResult(idEl, bridgeResp);
                }

                string offlineList = await BuildOfflineDiskWidgetsListJsonAsync();
                return BuildMcpTextToolResult(idEl, offlineList, isError: false);
            }

            case "deskbox_upsert_widget":
            {
                JsonElement mj = default;
                bool hasManifestJson = argsEl.ValueKind == JsonValueKind.Object &&
                                       argsEl.TryGetProperty("manifestJson", out mj) &&
                                       mj.ValueKind == JsonValueKind.String &&
                                       !string.IsNullOrWhiteSpace(mj.GetString());
                string? manifestJson = hasManifestJson ? mj.GetString() : null;
                string? widgetId = argsEl.ValueKind == JsonValueKind.Object &&
                                   argsEl.TryGetProperty("widgetId", out JsonElement wid) &&
                                   wid.ValueKind == JsonValueKind.String
                    ? wid.GetString()
                    : null;
                JsonElement rz = default;
                bool hasResize = argsEl.ValueKind == JsonValueKind.Object &&
                                 argsEl.TryGetProperty("resizeToDefault", out rz) &&
                                 rz.ValueKind is JsonValueKind.True or JsonValueKind.False;
                bool resize = hasResize && rz.ValueKind == JsonValueKind.True;
                // P2：这两个字段必须原样转发给 Bridge，否则「先暂存、确认后提交」在 MCP 通道上无法闭环。
                bool commit = argsEl.ValueKind == JsonValueKind.Object &&
                              argsEl.TryGetProperty("commit", out JsonElement cm) &&
                              cm.ValueKind == JsonValueKind.True;
                bool confirmDataLoss = argsEl.ValueKind == JsonValueKind.Object &&
                                       argsEl.TryGetProperty("confirmDataLoss", out JsonElement cdl) &&
                                       cdl.ValueKind == JsonValueKind.True;
                string? stageId = argsEl.ValueKind == JsonValueKind.Object &&
                                  argsEl.TryGetProperty("stageId", out JsonElement si) &&
                                  si.ValueKind == JsonValueKind.String
                    ? si.GetString()
                    : null;
                JsonElement ueUp = default;
                bool hasUpsertElements = argsEl.ValueKind == JsonValueKind.Object &&
                                         argsEl.TryGetProperty("upsertElements", out ueUp) &&
                                         ueUp.ValueKind == JsonValueKind.Object;
                JsonElement reUp = default;
                bool hasRemoveElements = argsEl.ValueKind == JsonValueKind.Object &&
                                         argsEl.TryGetProperty("removeElements", out reUp) &&
                                         reUp.ValueKind == JsonValueKind.Array &&
                                         reUp.GetArrayLength() > 0;
                JsonElement prUp = default;
                bool hasPatchRoot = argsEl.ValueKind == JsonValueKind.Object &&
                                    argsEl.TryGetProperty("patchRoot", out prUp) &&
                                    prUp.ValueKind == JsonValueKind.Object;
                JsonElement rrkUp = default;
                bool hasRemoveRootKeys = argsEl.ValueKind == JsonValueKind.Object &&
                                         argsEl.TryGetProperty("removeRootKeys", out rrkUp) &&
                                         rrkUp.ValueKind == JsonValueKind.Array &&
                                         rrkUp.GetArrayLength() > 0;
                bool hasFlatManifestEdits = hasUpsertElements || hasRemoveElements || hasPatchRoot || hasRemoveRootKeys;
                JsonElement shUp = default;
                bool hasScriptHunks = argsEl.ValueKind == JsonValueKind.Object &&
                                      argsEl.TryGetProperty("scriptHunks", out shUp) &&
                                      shUp.ValueKind == JsonValueKind.Array &&
                                      shUp.GetArrayLength() > 0;

                if (!commit && !hasManifestJson && !hasFlatManifestEdits && !hasScriptHunks)
                {
                    return BuildMcpTextToolResult(
                        idEl,
                        "manifestJson is required when staging a widget (commit=false).",
                        isError: true);
                }
                if (commit && string.IsNullOrWhiteSpace(stageId))
                {
                    return BuildMcpTextToolResult(
                        idEl,
                        "stageId is required when commit=true.",
                        isError: true);
                }

                JsonElement ljUp = default;
                bool hasLogicJs = argsEl.ValueKind == JsonValueKind.Object &&
                                  argsEl.TryGetProperty("logicJs", out ljUp) &&
                                  ljUp.ValueKind is JsonValueKind.String or JsonValueKind.Null;
                string? logicJs = hasLogicJs && ljUp.ValueKind == JsonValueKind.String ? ljUp.GetString() : null;
                JsonElement locUp = default;
                bool hasLocales = argsEl.ValueKind == JsonValueKind.Object &&
                                  argsEl.TryGetProperty("locales", out locUp) &&
                                  locUp.ValueKind is JsonValueKind.Object or JsonValueKind.String or JsonValueKind.Null;

                string? bridgeResp = await SendToBridgeEnsuringAppAsync(
                    w =>
                    {
                        w.WriteString("op", "upsert");
                        if (!string.IsNullOrWhiteSpace(widgetId))
                        {
                            w.WriteString("widgetId", widgetId);
                        }
                        if (hasManifestJson && manifestJson is not null)
                        {
                            w.WriteString("manifestJson", manifestJson);
                        }
                        if (hasUpsertElements)
                        {
                            w.WritePropertyName("upsertElements");
                            ueUp.WriteTo(w);
                        }
                        if (hasRemoveElements)
                        {
                            w.WritePropertyName("removeElements");
                            reUp.WriteTo(w);
                        }
                        if (hasPatchRoot)
                        {
                            w.WritePropertyName("patchRoot");
                            prUp.WriteTo(w);
                        }
                        if (hasRemoveRootKeys)
                        {
                            w.WritePropertyName("removeRootKeys");
                            rrkUp.WriteTo(w);
                        }
                        if (hasScriptHunks)
                        {
                            w.WritePropertyName("scriptHunks");
                            shUp.WriteTo(w);
                        }
                        if (hasLogicJs)
                        {
                            if (logicJs is null) w.WriteNull("logicJs");
                            else w.WriteString("logicJs", logicJs);
                        }
                        if (hasLocales)
                        {
                            if (locUp.ValueKind == JsonValueKind.Null)
                            {
                                w.WriteNull("locales");
                            }
                            else
                            {
                                w.WritePropertyName("locales");
                                locUp.WriteTo(w);
                            }
                        }
                        if (hasResize || !commit)
                        {
                            w.WriteBoolean("resizeToDefault", resize);
                        }
                        if (commit)
                        {
                            w.WriteBoolean("commit", true);
                            if (confirmDataLoss)
                            {
                                w.WriteBoolean("confirmDataLoss", true);
                            }
                            if (!string.IsNullOrWhiteSpace(stageId))
                            {
                                w.WriteString("stageId", stageId);
                            }
                        }
                        // 不主动写 reveal：Bridge 默认 false，外部工具不应自动改变桌面呈现。
                    },
                    pipeNameOverride,
                    autoLaunchAppIfOffline: pipeNameOverride is null && directBridgeSender is null,
                    cancellationToken,
                    directBridgeSender);

                if (bridgeResp is null)
                {
                    return BuildMcpTextToolResult(idEl, "DeskBox desktop host is not running and could not be reached.", isError: true);
                }

                return BuildMcpBridgeResult(idEl, bridgeResp);
            }

            case "deskbox_capture_widget_preview":
            {
                string widgetId = argsEl.ValueKind == JsonValueKind.Object &&
                                  argsEl.TryGetProperty("widgetId", out JsonElement wid) &&
                                  wid.ValueKind == JsonValueKind.String
                    ? wid.GetString() ?? string.Empty
                    : string.Empty;
                string? stageId = argsEl.ValueKind == JsonValueKind.Object &&
                                  argsEl.TryGetProperty("stageId", out JsonElement stEl) &&
                                  stEl.ValueKind == JsonValueKind.String
                    ? stEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(widgetId) && string.IsNullOrWhiteSpace(stageId))
                {
                    return BuildMcpTextToolResult(idEl, "Either widgetId or stageId is required.", isError: true);
                }

                string? theme = argsEl.ValueKind == JsonValueKind.Object &&
                                argsEl.TryGetProperty("theme", out JsonElement th) &&
                                th.ValueKind == JsonValueKind.String
                    ? th.GetString()
                    : null;
                string? mode = argsEl.ValueKind == JsonValueKind.Object &&
                               argsEl.TryGetProperty("mode", out JsonElement md) &&
                               md.ValueKind == JsonValueKind.String
                    ? md.GetString()
                    : null;

                string? bridgeResp = await SendToBridgeEnsuringAppAsync(
                    w =>
                    {
                        w.WriteString("op", "capture_preview");
                        w.WriteString("widgetId", widgetId);
                        if (!string.IsNullOrWhiteSpace(stageId))
                        {
                            w.WriteString("stageId", stageId);
                        }
                        if (!string.IsNullOrWhiteSpace(theme))
                        {
                            w.WriteString("theme", theme);
                        }
                        if (!string.IsNullOrWhiteSpace(mode))
                        {
                            w.WriteString("mode", mode);
                        }
                    },
                    pipeNameOverride,
                    autoLaunchAppIfOffline: pipeNameOverride is null && directBridgeSender is null,
                    cancellationToken,
                    directBridgeSender);

                if (bridgeResp is null)
                {
                    return BuildMcpTextToolResult(idEl, "DeskBox desktop host is not running.", isError: true);
                }

                using JsonDocument respDoc = JsonDocument.Parse(bridgeResp);
                JsonElement rRoot = respDoc.RootElement;
                bool ok = rRoot.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind == JsonValueKind.True;
                if (!ok)
                {
                    string err = rRoot.TryGetProperty("error", out JsonElement eEl) && eEl.ValueKind == JsonValueKind.String
                        ? eEl.GetString() ?? "Capture failed."
                        : "Capture failed.";
                    return BuildMcpTextToolResult(idEl, err, isError: true);
                }

                if (!rRoot.TryGetProperty("result", out JsonElement resObj) ||
                    resObj.ValueKind != JsonValueKind.Object ||
                    !resObj.TryGetProperty("base64Png", out JsonElement pngEl) ||
                    pngEl.ValueKind != JsonValueKind.String)
                {
                    return BuildMcpTextToolResult(idEl, "Capture response missing base64Png payload.", isError: true);
                }

                string base64Png = pngEl.GetString() ?? string.Empty;
                int width = resObj.TryGetProperty("width", out JsonElement wEl) ? wEl.GetInt32() : 0;
                int height = resObj.TryGetProperty("height", out JsonElement hEl) ? hEl.GetInt32() : 0;
                double dpiScale = resObj.TryGetProperty("dpiScale", out JsonElement dsEl) && dsEl.TryGetDouble(out double dsVal) && dsVal > 0 ? dsVal : 1.0;
                double measuredWidth = resObj.TryGetProperty("measuredWidth", out JsonElement mwEl) ? mwEl.GetDouble() : width;
                double measuredHeight = resObj.TryGetProperty("measuredHeight", out JsonElement mhEl) ? mhEl.GetDouble() : height;
                double verticalOverflowPx = resObj.TryGetProperty("verticalOverflowPx", out JsonElement ovEl) ? ovEl.GetDouble() : 0;
                bool hasOverflow = resObj.TryGetProperty("hasOverflow", out JsonElement hoEl) && hoEl.ValueKind == JsonValueKind.True;
                string resolvedMode = resObj.TryGetProperty("mode", out JsonElement mOutEl) && mOutEl.ValueKind == JsonValueKind.String
                    ? mOutEl.GetString() ?? "expanded"
                    : "expanded";
                string targetLabel = !string.IsNullOrWhiteSpace(widgetId) ? widgetId : (stageId ?? string.Empty);

                return BuildJsonRpcResult(idEl, writer =>
                {
                    writer.WriteBoolean("isError", false);
                    writer.WriteStartArray("content");
                    writer.WriteStartObject();
                    writer.WriteString("type", "image");
                    writer.WriteString("data", base64Png);
                    writer.WriteString("mimeType", "image/png");
                    writer.WriteEndObject();
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text",
                        $"Captured native WinUI 3 preview for '{targetLabel}' (bitmap={width}x{height}px @ {dpiScale:0.##}x DPI, mode={resolvedMode}, " +
                        $"measuredDesired={measuredWidth:0.#}x{measuredHeight:0.#}dip, verticalOverflowDip={verticalOverflowPx:0.#}, hasOverflow={hasOverflow.ToString().ToLowerInvariant()}).");
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                });
            }

            case "deskbox_push_widget_data":
            {
                string widgetId = argsEl.ValueKind == JsonValueKind.Object &&
                                  argsEl.TryGetProperty("widgetId", out JsonElement wid) &&
                                  wid.ValueKind == JsonValueKind.String
                    ? wid.GetString() ?? CustomWidgetTemplates.AgentHudTemplateId
                    : CustomWidgetTemplates.AgentHudTemplateId;

                string? bridgeResp = await SendToBridgeEnsuringAppAsync(
                    w =>
                    {
                        w.WriteString("op", "push_data");
                        w.WriteString("widgetId", widgetId);
                        if (argsEl.ValueKind == JsonValueKind.Object)
                        {
                            if (argsEl.TryGetProperty("state", out JsonElement st) && st.ValueKind == JsonValueKind.Object)
                            {
                                w.WritePropertyName("state");
                                st.WriteTo(w);
                            }
                            if (argsEl.TryGetProperty("collection", out JsonElement col) && col.ValueKind == JsonValueKind.String)
                            {
                                w.WriteString("collection", col.GetString());
                            }
                            if (argsEl.TryGetProperty("mode", out JsonElement md) && md.ValueKind == JsonValueKind.String)
                            {
                                w.WriteString("mode", md.GetString());
                            }
                            if (argsEl.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
                            {
                                w.WritePropertyName("items");
                                items.WriteTo(w);
                            }
                        }
                    },
                    pipeNameOverride,
                    autoLaunchAppIfOffline: pipeNameOverride is null && directBridgeSender is null,
                    cancellationToken,
                    directBridgeSender);

                if (bridgeResp is null)
                {
                    return BuildMcpTextToolResult(idEl, "DeskBox desktop host is not running.", isError: true);
                }

                return BuildMcpBridgeResult(idEl, bridgeResp);
            }

            default:
                return BuildJsonRpcError(idEl, -32602, $"Unknown tool '{toolName}'.");
        }
    }

    private static async Task<string?> SendToBridgeEnsuringAppAsync(
        Action<Utf8JsonWriter> writePayload,
        string? pipeNameOverride,
        bool autoLaunchAppIfOffline,
        CancellationToken cancellationToken,
        Func<Action<Utf8JsonWriter>, CancellationToken, Task<string?>>? directBridgeSender = null)
    {
        if (directBridgeSender is not null)
        {
            return await directBridgeSender(writePayload, cancellationToken);
        }
        string? resp = await DeskBoxAgentBridgeClient.TrySendAsync(
            writePayload,
            pipeNameOverride,
            connectTimeoutMs: 1200,
            cancellationToken: cancellationToken);
        if (resp is not null || !autoLaunchAppIfOffline)
        {
            return resp;
        }

        try
        {
            string? exePath = CustomWidgetAgentConnectorService.ResolveAutoLaunchExecutablePath();
            bool launched = false;
            using (ProcessEnvironmentSyncService.AcquireChildLaunchContaminationScope())
            {
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--startup",
                        WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                        UseShellExecute = false
                    };
                    psi.Environment.Remove("ELECTRON_RUN_AS_NODE");
                    psi.Environment.Remove("__COMPAT_LAYER");
                    Process.Start(psi);
                    launched = true;
                }
                else if (AppDistributionService.Current.IsPackaged ||
                         AppDistributionService.Current.IsMicrosoftStore)
                {
                    // Fallback when Windows AppExecutionAlias is disabled: activate via registered protocol.
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "deskbox://startup",
                        UseShellExecute = true
                    });
                    launched = true;
                }
            }

            if (launched)
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    await Task.Delay(500, cancellationToken);
                    resp = await DeskBoxAgentBridgeClient.TrySendAsync(
                        writePayload,
                        pipeNameOverride,
                        connectTimeoutMs: 800,
                        cancellationToken: cancellationToken);
                    if (resp is not null)
                    {
                        return resp;
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string BuildOfflineStudioContextJson()
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", true);
            DeskBoxAgentBridgeServer.WriteCommonStudioContextHeader(w, hostOnline: false, activeCulture: null);
            w.WriteBoolean("isOpen", false);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildCapabilitySpecText(
        string? capId,
        string? query,
        bool fullRef,
        string? language,
        out bool isError)
    {
        isError = false;
        bool wantZh = !string.IsNullOrWhiteSpace(language) &&
                      CustomWidgetRemotePromptService.IsChineseCulture(language);
        if (fullRef)
        {
            return CustomWidgetCapabilityRegistry.GenerateApiReferenceMarkdown(isChinese: wantZh);
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(capId))
        {
            IReadOnlyList<CustomWidgetCapabilitySpec> expanded =
                CustomWidgetCapabilityRegistry.ExpandPackOrCapabilityIds([capId.Trim()]);
            if (expanded.Count == 0)
            {
                isError = true;
                return $"Capability '{capId}' not found. Call deskbox_get_capability_spec with no arguments to list all capability IDs.";
            }

            sb.AppendLine(CustomWidgetCapabilityRegistry.FormatSelectedCapsulesPromptBlock(expanded, []));
            return sb.ToString();
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            IReadOnlyList<CustomWidgetCapabilitySpec> matches = CustomWidgetCapabilityRegistry.Search(query).Take(5).ToList();
            if (matches.Count == 0)
            {
                sb.AppendLine($"No capability matched '{query}'. Showing Tier-0 capability directory:");
                sb.AppendLine();
                sb.AppendLine(CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock());
                return sb.ToString();
            }

            IReadOnlyList<CustomWidgetCapabilitySpec> withDeps =
                CustomWidgetCapabilityRegistry.ExpandWithDependencies(matches.Select(static m => m.Id));
            sb.AppendLine(CustomWidgetCapabilityRegistry.FormatSelectedCapsulesPromptBlock([], withDeps));
            return sb.ToString();
        }

        sb.AppendLine(CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock());
        sb.AppendLine();
        sb.AppendLine("## Available Capability Capsules (pass `capabilityId` or `query` for full contract + snippet)");
        foreach (CustomWidgetCapabilitySpec spec in CustomWidgetCapabilityRegistry.All)
        {
            sb.Append("- `").Append(spec.Id).Append("` (").Append(spec.Emoji).Append(' ')
              .Append(wantZh ? spec.TitleZh : spec.TitleEn)
              .Append("): ")
              .AppendLine(wantZh ? spec.SummaryZh : spec.SummaryEn);
        }

        return sb.ToString();
    }

    private static async Task<string> BuildOfflineDiskWidgetsListJsonAsync()
    {
        string root = CustomWidgetDataStore.GetWidgetsRootDirectory();
        var items = new List<(string FolderName, CustomWidgetManifest? Manifest, string Dir, string ManifestPath)>();
        if (Directory.Exists(root))
        {
            foreach (string dir in Directory.GetDirectories(root))
            {
                string manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                string folderName = Path.GetFileName(dir);
                CustomWidgetManifest? m = null;
                try
                {
                    string raw = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8);
                    m = CustomWidgetManifestParser.Parse(raw).Manifest;
                }
                catch
                {
                }
                items.Add((folderName, m, dir, manifestPath));
            }
        }

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("status", "offline-disk-scan");
            w.WriteString("widgetsRoot", root);
            w.WriteStartArray("widgets");
            foreach (var item in items)
            {
                DeskBoxAgentBridgeServer.WriteWidgetListItemJson(
                    w,
                    item.FolderName,
                    item.Manifest?.Name ?? item.FolderName,
                    enabled: null,
                    item.Manifest,
                    item.Dir,
                    item.ManifestPath);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static async Task<string?> BuildOfflineDiskSingleWidgetJsonAsync(string widgetIdOrManifestId)
    {
        string root = CustomWidgetDataStore.GetWidgetsRootDirectory();
        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (string dir in Directory.GetDirectories(root))
        {
            string manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            string folderName = Path.GetFileName(dir);
            string rawJson;
            CustomWidgetManifest? m;
            try
            {
                rawJson = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8);
                m = CustomWidgetManifestParser.Parse(rawJson).Manifest;
            }
            catch
            {
                continue;
            }

            if (!string.Equals(folderName, widgetIdOrManifestId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(m?.Id, widgetIdOrManifestId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string logicPath = Path.Combine(dir, CustomWidgetDataStore.LogicJsFileName);
            string? logicJs = null;
            if (File.Exists(logicPath))
            {
                try { logicJs = await File.ReadAllTextAsync(logicPath, Encoding.UTF8); } catch { }
            }
            string localesDir = Path.Combine(dir, CustomWidgetDataStore.LocalesDirectoryName);
            CustomWidgetLocaleCatalog? offlineLocales = CustomWidgetLocaleCatalog.LoadFromDirectory(localesDir);

            var buffer = new ArrayBufferWriter<byte>(1024);
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                DeskBoxAgentBridgeServer.WriteWidgetInspectionBodyJson(
                    w,
                    folderName,
                    m?.Name ?? folderName,
                    m,
                    visible: null,
                    dir,
                    manifestPath,
                    rawJson,
                    logicPath: null,
                    logicJs,
                    localesDir,
                    offlineLocales,
                    liveSnapshot: null,
                    diskPayload: null,
                    isOfflineScan: true);
                w.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        return null;
    }

    private static void WriteCliValueAsTypedOrStringJson(Utf8JsonWriter writer, string propertyName, string rawValue)
    {
        string trimmed = rawValue.Trim();
        if (trimmed.Length > 0)
        {
            char first = trimmed[0];
            bool maybeTypedJson =
                string.Equals(trimmed, "true", StringComparison.Ordinal) ||
                string.Equals(trimmed, "false", StringComparison.Ordinal) ||
                first is '[' or '{' or '-' ||
                char.IsAsciiDigit(first);
            if (maybeTypedJson)
            {
                try
                {
                    using JsonDocument parsed = JsonDocument.Parse(trimmed);
                    if (parsed.RootElement.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Array or JsonValueKind.Object)
                    {
                        writer.WritePropertyName(propertyName);
                        parsed.RootElement.WriteTo(writer);
                        return;
                    }
                }
                catch (JsonException)
                {
                }
            }
        }

        writer.WriteString(propertyName, rawValue);
    }

    public static async Task<int> RunWidgetCliAsync(string[] args, string? pipeNameOverride = null)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        switch (sub)
        {
            case "status":
            case "doctor":
            {
                string? resp = await DeskBoxAgentBridgeClient.TrySendAsync(
                    w => w.WriteString("op", "ping"),
                    pipeNameOverride);
                if (resp is not null)
                {
                    Console.WriteLine(resp);
                    return 0;
                }

                Console.WriteLine(DeskBoxAgentBridgeServer.BuildErrorJson("DeskBox desktop host is offline."));
                return 1;
            }

            case "spec":
                Console.WriteLine(CustomWidgetTemplates.AiAgentSkillMarkdown);
                return 0;

            case "capability-spec":
            case "capabilities":
            {
                string? capId = GetFlagValue(args, "--id") ?? GetFlagValue(args, "--capability");
                string? query = GetFlagValue(args, "--query");
                string? lang = GetFlagValue(args, "--lang") ?? GetFlagValue(args, "--language");
                bool full = args.Any(static a => string.Equals(a, "--full", StringComparison.OrdinalIgnoreCase));
                string text = BuildCapabilitySpecText(capId, query, full, lang, out bool capErr);
                if (capErr)
                {
                    Console.Error.WriteLine(text);
                    return 1;
                }
                Console.WriteLine(text);
                return 0;
            }

            case "studio-context":
            {
                string? incFlag = GetFlagValue(args, "--include-manifest");
                bool noManifest = args.Any(static a => string.Equals(a, "--no-manifest", StringComparison.OrdinalIgnoreCase)) ||
                                  string.Equals(incFlag, "false", StringComparison.OrdinalIgnoreCase);
                bool includeManifest = !noManifest;
                string? resp = await DeskBoxAgentBridgeClient.TrySendAsync(
                    w =>
                    {
                        w.WriteString("op", "studio_context");
                        w.WriteBoolean("includeManifest", includeManifest);
                    },
                    pipeNameOverride);
                Console.WriteLine(resp ?? BuildOfflineStudioContextJson());
                return 0;
            }

            case "schema":
                Console.WriteLine(CustomWidgetTemplates.ManifestJsonSchema);
                return 0;

            case "list":
            {
                string? resp = await DeskBoxAgentBridgeClient.TrySendAsync(
                    w => w.WriteString("op", "list"),
                    pipeNameOverride);
                Console.WriteLine(resp ?? await BuildOfflineDiskWidgetsListJsonAsync());
                return 0;
            }

            case "get":
            {
                string? id = GetFlagValue(args, "--id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    Console.Error.WriteLine("Usage: DeskBox.exe widget get --id <widgetId>");
                    return 2;
                }

                string? resp = await DeskBoxAgentBridgeClient.TrySendAsync(
                    w =>
                    {
                        w.WriteString("op", "get");
                        w.WriteString("widgetId", id);
                    },
                    pipeNameOverride);
                if (resp is null)
                {
                    string? offlineSingle = await BuildOfflineDiskSingleWidgetJsonAsync(id);
                    if (offlineSingle is not null)
                    {
                        Console.WriteLine(offlineSingle);
                        return 0;
                    }

                    Console.Error.WriteLine("DeskBox is not currently running and widget was not found on disk.");
                    return 1;
                }
                Console.WriteLine(resp);
                return IsBridgeOk(resp) ? 0 : 1;
            }

            case "validate":
            {
                string? file = GetFlagValue(args, "--file");
                string? rawJson = GetFlagValue(args, "--json");
                if (!string.IsNullOrWhiteSpace(file))
                {
                    if (!File.Exists(file))
                    {
                        Console.Error.WriteLine($"File not found: {file}");
                        return 2;
                    }
                    rawJson = await File.ReadAllTextAsync(file, Encoding.UTF8);
                }

                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    Console.Error.WriteLine("Usage: DeskBox.exe widget validate --file <manifest.json>");
                    return 2;
                }

                string? logicFile = GetFlagValue(args, "--logic");
                string? localesDir = GetFlagValue(args, "--locales-dir");
                if (!string.IsNullOrWhiteSpace(file))
                {
                    string? baseDir = Path.GetDirectoryName(Path.GetFullPath(file));
                    if (string.IsNullOrWhiteSpace(logicFile) && baseDir is not null)
                    {
                        string siblingLogic = Path.Combine(baseDir, CustomWidgetDataStore.LogicJsFileName);
                        if (File.Exists(siblingLogic)) logicFile = siblingLogic;
                    }
                    if (string.IsNullOrWhiteSpace(localesDir) && baseDir is not null)
                    {
                        string siblingLocales = Path.Combine(baseDir, CustomWidgetDataStore.LocalesDirectoryName);
                        if (Directory.Exists(siblingLocales)) localesDir = siblingLocales;
                    }
                }

                string? logicJs = !string.IsNullOrWhiteSpace(logicFile) && File.Exists(logicFile)
                    ? await File.ReadAllTextAsync(logicFile, Encoding.UTF8)
                    : null;
                List<CustomWidgetValidationDiagnostic>? extraLocaleDiags = null;
                CustomWidgetLocaleCatalog? locales = null;
                if (!string.IsNullOrWhiteSpace(localesDir) && Directory.Exists(localesDir))
                {
                    extraLocaleDiags = CustomWidgetScriptValidator.ValidateRawLocaleDirectoryFiles(localesDir).ToList();
                    locales = CustomWidgetLocaleCatalog.LoadFromDirectory(localesDir);
                }

                string reportJson = DeskBoxAgentBridgeServer.BuildValidationReportJson(
                    rawJson,
                    logicJs,
                    locales,
                    extraLocaleDiags,
                    indented: true,
                    out bool valid);
                Console.WriteLine(reportJson);
                return valid ? 0 : 1;
            }

            case "create":
            case "update":
            case "upsert":
            case "apply":
            {
                string? id = GetFlagValue(args, "--id");
                string? file = GetFlagValue(args, "--file");
                bool explicitCommit = args.Any(static a => string.Equals(a, "--commit", StringComparison.OrdinalIgnoreCase));
                bool confirmDataLoss = args.Any(static a => string.Equals(a, "--confirm-data-loss", StringComparison.OrdinalIgnoreCase));
                string? stageId = GetFlagValue(args, "--stage-id");
                if (explicitCommit && string.IsNullOrWhiteSpace(stageId))
                {
                    Console.Error.WriteLine("widget apply --commit requires --stage-id <stageId> from the preceding staged call.");
                    return 2;
                }

                bool hasFile = !string.IsNullOrWhiteSpace(file);
                if (!explicitCommit && (!hasFile || !File.Exists(file!)))
                {
                    Console.Error.WriteLine($"Usage: DeskBox.exe widget {sub} [--id <widgetId>] --file <manifest.json> [--commit --stage-id <stageId> [--confirm-data-loss]]");
                    return 2;
                }
                if (hasFile && !File.Exists(file!))
                {
                    Console.Error.WriteLine($"File not found: {file}");
                    return 2;
                }

                string? rawJson = hasFile ? await File.ReadAllTextAsync(file!, Encoding.UTF8) : null;
                string? logicFile = GetFlagValue(args, "--logic");
                string? localesDir = GetFlagValue(args, "--locales-dir");
                string? baseDir = hasFile ? Path.GetDirectoryName(Path.GetFullPath(file!)) : null;
                if (string.IsNullOrWhiteSpace(logicFile) && baseDir is not null)
                {
                    string siblingLogic = Path.Combine(baseDir, CustomWidgetDataStore.LogicJsFileName);
                    if (File.Exists(siblingLogic)) logicFile = siblingLogic;
                }
                if (string.IsNullOrWhiteSpace(localesDir) && baseDir is not null)
                {
                    string siblingLocales = Path.Combine(baseDir, CustomWidgetDataStore.LocalesDirectoryName);
                    if (Directory.Exists(siblingLocales)) localesDir = siblingLocales;
                }
                bool hasLogicArg = !string.IsNullOrWhiteSpace(logicFile) && File.Exists(logicFile);
                string? logicJs = hasLogicArg ? await File.ReadAllTextAsync(logicFile!, Encoding.UTF8) : null;
                CustomWidgetLocaleCatalog? locales = !string.IsNullOrWhiteSpace(localesDir) && Directory.Exists(localesDir)
                    ? CustomWidgetLocaleCatalog.LoadFromDirectory(localesDir)
                    : null;
                bool hasLocalesArg = locales is { HasAnyLocale: true };
                bool hasResizeFlag = args.Any(static a => string.Equals(a, "--resize", StringComparison.OrdinalIgnoreCase));

                string? resp = await SendToBridgeEnsuringAppAsync(
                    w =>
                    {
                        w.WriteString("op", "upsert");
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            w.WriteString("widgetId", id);
                        }
                        if (!string.IsNullOrWhiteSpace(rawJson))
                        {
                            w.WriteString("manifestJson", rawJson);
                        }
                        if (hasLogicArg && logicJs is not null)
                        {
                            w.WriteString("logicJs", logicJs);
                        }
                        if (hasLocalesArg && locales is not null)
                        {
                            w.WritePropertyName("locales");
                            locales.WriteCatalogObjectTo(w);
                        }
                        if (hasResizeFlag || !explicitCommit)
                        {
                            w.WriteBoolean("resizeToDefault", hasResizeFlag);
                        }
                        if (explicitCommit)
                        {
                            w.WriteBoolean("commit", true);
                            if (confirmDataLoss)
                            {
                                w.WriteBoolean("confirmDataLoss", true);
                            }
                            w.WriteString("stageId", stageId);
                        }
                        // reveal 默认不写：Bridge 默认 false，CLI 不主动改变桌面呈现。
                    },
                    pipeNameOverride,
                    autoLaunchAppIfOffline: pipeNameOverride is null,
                    CancellationToken.None);

                if (resp is null)
                {
                    Console.Error.WriteLine("DeskBox desktop host could not be reached.");
                    return 1;
                }

                Console.WriteLine(resp);
                return IsBridgeOk(resp) ? 0 : 1;
            }

            case "preview":
            case "capture":
            case "screenshot":
            {
                string? id = GetFlagValue(args, "--id");
                string? stageId = GetFlagValue(args, "--stage-id");
                string? outPath = GetFlagValue(args, "--out");
                string? theme = GetFlagValue(args, "--theme");
                string? mode = GetFlagValue(args, "--mode");
                if ((string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(stageId)) || string.IsNullOrWhiteSpace(outPath))
                {
                    Console.Error.WriteLine("Usage: DeskBox.exe widget preview (--id <widgetId> | --stage-id <stageId>) --out <preview.png> [--theme dark|light] [--mode expanded|capsule|live]");
                    return 2;
                }

                string? resp = await SendToBridgeEnsuringAppAsync(
                    w =>
                    {
                        w.WriteString("op", "capture_preview");
                        w.WriteString("widgetId", id ?? string.Empty);
                        if (!string.IsNullOrWhiteSpace(stageId))
                        {
                            w.WriteString("stageId", stageId);
                        }
                        if (!string.IsNullOrWhiteSpace(theme))
                        {
                            w.WriteString("theme", theme);
                        }
                        if (!string.IsNullOrWhiteSpace(mode))
                        {
                            w.WriteString("mode", mode);
                        }
                    },
                    pipeNameOverride,
                    autoLaunchAppIfOffline: pipeNameOverride is null,
                    CancellationToken.None);

                if (resp is null || !IsBridgeOk(resp))
                {
                    Console.Error.WriteLine(resp ?? "DeskBox desktop host could not be reached.");
                    return 1;
                }

                using JsonDocument doc = JsonDocument.Parse(resp);
                JsonElement resEl = doc.RootElement.GetProperty("result");
                string b64 = resEl.TryGetProperty("base64Png", out JsonElement b64El)
                    ? b64El.GetString() ?? string.Empty
                    : string.Empty;
                byte[] pngBytes = Convert.FromBase64String(b64);
                string fullOut = Path.GetFullPath(outPath);
                if (DeskBox.Services.DragDrop.StoreVirtualizedPathPolicy.IsWriteBlockedDestination(fullOut))
                {
                    Console.Error.WriteLine(
                        "Cannot write preview PNG inside AppData/LocalAppData in the Microsoft Store build because MSIX virtualizes those folders.");
                    return 1;
                }
                string? parent = Path.GetDirectoryName(fullOut);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                await File.WriteAllBytesAsync(fullOut, pngBytes);
                if (resEl.TryGetProperty("hasOverflow", out JsonElement ovFlag) && ovFlag.ValueKind == JsonValueKind.True)
                {
                    double ovPx = resEl.TryGetProperty("verticalOverflowPx", out JsonElement ovPxEl) ? ovPxEl.GetDouble() : 0;
                    Console.Error.WriteLine($"Warning: captured widget overflows vertically by ~{ovPx:0.#}px.");
                }
                Console.WriteLine(fullOut);
                return 0;
            }

            case "push":
            {
                string id = GetFlagValue(args, "--id") ?? CustomWidgetTemplates.AgentHudTemplateId;
                var stateDict = new Dictionary<string, string>(StringComparer.Ordinal);
                string? collection = null;
                string? mode = null;
                var items = new List<Dictionary<string, string>>();

                for (int i = 1; i < args.Length; i++)
                {
                    if ((string.Equals(args[i], "--set", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(args[i], "--state", StringComparison.OrdinalIgnoreCase)) &&
                        i + 1 < args.Length)
                    {
                        string kv = args[++i];
                        int eq = kv.IndexOf('=');
                        if (eq > 0)
                        {
                            stateDict[kv[..eq].Trim()] = kv[(eq + 1)..];
                        }
                    }
                    else if ((string.Equals(args[i], "--prepend", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(args[i], "--append", StringComparison.OrdinalIgnoreCase)) &&
                             i + 1 < args.Length)
                    {
                        mode = args[i].TrimStart('-').ToLowerInvariant();
                        string spec = args[++i];
                        int colon = spec.IndexOf(':');
                        if (colon > 0)
                        {
                            collection = spec[..colon].Trim();
                            var record = new Dictionary<string, string>(StringComparer.Ordinal);
                            string rawPairs = spec[(colon + 1)..];
                            string[] pairs = rawPairs.Contains(';')
                                ? rawPairs.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                : CliPairDelimiterRegex().Split(rawPairs);
                            foreach (string pair in pairs)
                            {
                                int eq = pair.IndexOf('=');
                                if (eq > 0)
                                {
                                    record[pair[..eq].Trim()] = pair[(eq + 1)..];
                                }
                            }
                            if (record.Count == 0)
                            {
                                int eq = rawPairs.IndexOf('=');
                                if (eq < 0 && rawPairs.Length > 0)
                                {
                                    record["text"] = rawPairs;
                                }
                            }
                            items.Add(record);
                        }
                    }
                    else if (string.Equals(args[i], "--clear", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        collection = args[++i].Trim();
                        mode = "clear";
                    }
                }

                stateDict.TryAdd("updatedAt", DateTimeOffset.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));

                string? resp = await SendToBridgeEnsuringAppAsync(
                    w =>
                    {
                        w.WriteString("op", "push_data");
                        w.WriteString("widgetId", id);
                        if (stateDict.Count > 0)
                        {
                            w.WriteStartObject("state");
                            foreach ((string k, string v) in stateDict)
                            {
                                WriteCliValueAsTypedOrStringJson(w, k, v);
                            }
                            w.WriteEndObject();
                        }
                        if (!string.IsNullOrWhiteSpace(collection))
                        {
                            w.WriteString("collection", collection);
                            w.WriteString("mode", mode ?? "prepend");
                            w.WriteStartArray("items");
                            foreach (Dictionary<string, string> rec in items)
                            {
                                w.WriteStartObject();
                                foreach ((string fk, string fv) in rec)
                                {
                                    WriteCliValueAsTypedOrStringJson(w, fk, fv);
                                }
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                        }
                    },
                    pipeNameOverride,
                    autoLaunchAppIfOffline: pipeNameOverride is null,
                    CancellationToken.None);

                if (resp is null)
                {
                    Console.Error.WriteLine("DeskBox desktop host could not be reached.");
                    return 1;
                }

                Console.WriteLine(resp);
                return IsBridgeOk(resp) ? 0 : 1;
            }

            default:
                Console.WriteLine("""
DeskBox Custom Widget Agent CLI
Usage:
  DeskBox.exe --mcp                                        Start MCP stdio server
  DeskBox.exe widget spec                                  Print SKILL.md developer guide
  DeskBox.exe widget capability-spec [--id <cap> | --query <q> | --full] [--lang <culture>]
                                                          Query Single-Source Capability Registry or full API reference
  DeskBox.exe widget studio-context [--no-manifest]        Inspect active Widget Workshop session & pending stages
  DeskBox.exe widget schema                                Print manifest.schema.json
  DeskBox.exe widget list                                  List Custom Widgets
  DeskBox.exe widget get --id <id>                         Inspect widget manifest & state
  DeskBox.exe widget validate --file <manifest.json>       Validate manifest with runtime hints
  DeskBox.exe widget create --file <manifest.json>         Validate and STAGE a widget (desktop unchanged, prints stageId)
  DeskBox.exe widget update --id <id> --file <manifest>    Validate and STAGE an update (desktop unchanged, prints stageId)
  DeskBox.exe widget apply --commit --stage-id <stageId>   Commit a previously staged change after user confirmation
  DeskBox.exe widget preview --id <id> --out <file.png>    Capture native WinUI PNG screenshot
  DeskBox.exe widget push --id agent-hud --set statusText="Running" --set progress=60 --prepend events:tag=BUILD;text="Tests green"
""");
                return 0;
        }
    }

    private static string? GetFlagValue(string[] args, string flagName)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flagName, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static bool IsBridgeOk(string responseJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.TryGetProperty("ok", out JsonElement okEl) &&
                   okEl.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildMcpBridgeResult(JsonElement idEl, string bridgeRespJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(bridgeRespJson);
            bool ok = doc.RootElement.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                string err = doc.RootElement.TryGetProperty("error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.String
                    ? errEl.GetString() ?? "Operation failed."
                    : "Operation failed.";
                return BuildMcpTextToolResult(idEl, err, isError: true);
            }

            if (doc.RootElement.TryGetProperty("result", out JsonElement resEl))
            {
                return BuildMcpTextToolResult(idEl, resEl.GetRawText(), isError: false);
            }
        }
        catch
        {
        }

        return BuildMcpTextToolResult(idEl, bridgeRespJson, isError: false);
    }

    private static string BuildMcpTextToolResult(JsonElement idEl, string text, bool isError)
    {
        return BuildJsonRpcResult(idEl, writer =>
        {
            writer.WriteBoolean("isError", isError);
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
        });
    }

    private static string BuildJsonRpcResult(JsonElement idEl, Action<Utf8JsonWriter> writeResult)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            idEl.WriteTo(writer);
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writeResult(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildJsonRpcError(JsonElement? idEl, int code, string message)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            if (idEl is { } id && id.ValueKind is not JsonValueKind.Undefined)
            {
                writer.WritePropertyName("id");
                id.WriteTo(writer);
            }
            else
            {
                writer.WriteNull("id");
            }
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@",(?=[A-Za-z_][A-Za-z0-9_]*=)", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex CliPairDelimiterRegex();
}
