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
public static class DeskBoxAgentCliEntry
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
        using var reader = new StreamReader(stdin, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 32 * 1024, leaveOpen: true);

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

            string? responseJson = await HandleMcpRpcMessageAsync(messageJson, pipeNameOverride, cancellationToken);
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

    private static async Task<(string? Json, bool IsContentLengthFramed)> ReadNextRpcMessageAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        string? line = await reader.ReadLineAsync(cancellationToken);
        if (line is null)
        {
            return (null, false);
        }

        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
        {
            string lenStr = line["Content-Length:".Length..].Trim();
            if (!int.TryParse(lenStr, out int byteLength) || byteLength < 0 || byteLength > 16 * 1024 * 1024)
            {
                return (string.Empty, true);
            }

            while (true)
            {
                string? headerLine = await reader.ReadLineAsync(cancellationToken);
                if (headerLine is null || headerLine.Length == 0)
                {
                    break;
                }
            }

            char[] buffer = new char[byteLength];
            int readTotal = 0;
            while (readTotal < byteLength)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(readTotal, byteLength - readTotal), cancellationToken);
                if (read <= 0)
                {
                    break;
                }
                readTotal += read;
            }

            return (new string(buffer, 0, readTotal), true);
        }

        return (line, false);
    }

    public sealed record McpToolSummary(string Name, string DescriptionKey, string FallbackDescription);

    public static IReadOnlyList<McpToolSummary> GetMcpToolSummaries() =>
    [
        new("deskbox_get_widget_spec", "CustomWidget.AgentHub.Tool.GetSpec", "Read SKILL.md specification & JSON Schema"),
        new("deskbox_get_capability_spec", "CustomWidget.AgentHub.Tool.GetCapabilitySpec", "Search or inspect atomic capability contracts & API Reference"),
        new("deskbox_list_widgets", "CustomWidget.AgentHub.Tool.ListWidgets", "List live desktop widgets & workspace directories"),
        new("deskbox_validate_manifest", "CustomWidget.AgentHub.Tool.Validate", "Validate manifest.json syntax, nodes & bindings"),
        new("deskbox_upsert_widget", "CustomWidget.AgentHub.Tool.Upsert", "Create or hot-reload a widget on desktop"),
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
            doc = JsonDocument.Parse(requestJson);
        }
        catch (Exception ex)
        {
            return BuildJsonRpcError(null, -32700, $"Parse error: {ex.Message}");
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            bool hasId = root.TryGetProperty("id", out JsonElement idEl) &&
                         idEl.ValueKind is not JsonValueKind.Undefined;
            string method = root.TryGetProperty("method", out JsonElement mEl) && mEl.ValueKind == JsonValueKind.String
                ? mEl.GetString() ?? string.Empty
                : string.Empty;

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
                "initialize" => BuildJsonRpcResult(idEl, writer =>
                {
                    string requestedVersion = paramsEl is { } p &&
                                              p.TryGetProperty("protocolVersion", out JsonElement pv) &&
                                              pv.ValueKind == JsonValueKind.String
                        ? pv.GetString() ?? "2024-11-05"
                        : "2024-11-05";
                    writer.WriteString("protocolVersion", requestedVersion);
                    writer.WriteStartObject("capabilities");
                    writer.WriteStartObject("tools");
                    writer.WriteBoolean("listChanged", false);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteStartObject("serverInfo");
                    writer.WriteString("name", "deskbox");
                    writer.WriteString("version", "1.0.0");
                    writer.WriteEndObject();
                }),

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
    }

    private static void WriteMcpToolsCatalog(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("tools");

        // 1. deskbox_get_widget_spec
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_get_widget_spec");
        writer.WriteString("description", "Get DeskBox's native WinUI 3 Custom Widget developer specification (SKILL.md), Tier-0 capability directory, and optional JSON Schema. Call this or deskbox_get_capability_spec before designing or editing a DeskBox widget.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        writer.WriteStartObject("includeSchema");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "Include full Draft 2020-12 JSON Schema in addition to SKILL.md.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        // 1b. deskbox_get_capability_spec
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_get_capability_spec");
        writer.WriteString("description", "Query DeskBox's Single-Source-of-Truth Capability Registry (19 atomic capsules across weather, calendar/lunar, systemMetrics, deviceState/system.control, file.transform/DropZone, secret vault + authenticated httpJson headers, aiMonitor, countdown, pomodoro, charts, etc.). Pass 'capabilityId' or 'query' to get exact tokens, properties, auto-linked dependencies, and Golden JSON snippets, or omit both to list all capabilities.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        writer.WriteStartObject("capabilityId");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Exact capability ID (e.g. 'weather-source', 'calendar-lunar', 'calendar-grid', 'system-control', 'file-transform', 'drop-zone', 'secret-vault', 'network-feeds', 'ai-monitor'). Automatically expands required dependencies.");
        writer.WriteEndObject();
        writer.WriteStartObject("query");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Keyword search across capability titles, $namespace.* tokens, node types, and action types (e.g. 'weather', 'lunar', 'volume', 'webp', 'secret').");
        writer.WriteEndObject();
        writer.WriteStartObject("includeFullReference");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "When true, returns the complete API_REFERENCE.md Markdown document.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        // 2. deskbox_list_widgets
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_list_widgets");
        writer.WriteString("description", "List all Custom Widgets in DeskBox (widgetId, name, icon, workspace directory, manifest.json path), or pass 'widgetId' to inspect one widget's full manifest.json and live runtime state (including sourceStatus/sourceError, displayMode, collectionCounts, and sampleItems).");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        writer.WriteStartObject("widgetId");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Optional widgetId or manifest id. When provided, inspects that widget's manifest.json and live runtime state/collections.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        // 3. deskbox_validate_manifest
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_validate_manifest");
        writer.WriteString("description", "Validate a DeskBox Custom Widget manifest.json string, checking JSON syntax, supported WinUI components/actions, and undeclared state/settings/collections references.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartArray("required");
        writer.WriteStringValue("manifestJson");
        writer.WriteEndArray();
        writer.WriteStartObject("properties");
        writer.WriteStartObject("manifestJson");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Full manifest.json content to validate.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        // 4. deskbox_upsert_widget
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_upsert_widget");
        writer.WriteString("description", "Create a new DeskBox desktop Custom Widget or hot-reload an existing one on the user's Windows 11 desktop.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartArray("required");
        writer.WriteStringValue("manifestJson");
        writer.WriteEndArray();
        writer.WriteStartObject("properties");
        writer.WriteStartObject("widgetId");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Optional existing widgetId (or manifest id) to update. Omit to create a new widget.");
        writer.WriteEndObject();
        writer.WriteStartObject("manifestJson");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Valid manifest.json content.");
        writer.WriteEndObject();
        writer.WriteStartObject("resizeToDefault");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "Resize existing widget window to manifest defaultSize.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        // 5. deskbox_capture_widget_preview
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_capture_widget_preview");
        writer.WriteString("description", "Capture a real native WinUI 3 PNG screenshot of a live DeskBox Custom Widget so you can visually inspect layout, typography, spacing, and Light/Dark theme contrast. Defaults to 'expanded' mode so full UI is rendered even when the desktop is in Capsule Mode.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartArray("required");
        writer.WriteStringValue("widgetId");
        writer.WriteEndArray();
        writer.WriteStartObject("properties");
        writer.WriteStartObject("widgetId");
        writer.WriteString("type", "string");
        writer.WriteString("description", "Target widgetId returned by deskbox_list_widgets or deskbox_upsert_widget.");
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
        writer.WriteEndObject();

        // 6. deskbox_push_widget_data
        writer.WriteStartObject();
        writer.WriteString("name", "deskbox_push_widget_data");
        writer.WriteString("description", "Push live reactive state key-values or collection records to a DeskBox Custom Widget (e.g. 'agent-hud' Ambient Agent HUD) in real time.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
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
                var sb = new StringBuilder();
                sb.AppendLine($"Widgets Workspace Root: {CustomWidgetDataStore.GetWidgetsRootDirectory()}");
                sb.AppendLine();
                sb.AppendLine(CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock());
                sb.AppendLine();
                sb.AppendLine(CustomWidgetTemplates.AiAgentSkillMarkdown);
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

            case "deskbox_get_capability_spec":
            {
                bool fullRef = argsEl.ValueKind == JsonValueKind.Object &&
                               argsEl.TryGetProperty("includeFullReference", out JsonElement fullEl) &&
                               fullEl.ValueKind == JsonValueKind.True;
                if (fullRef)
                {
                    return BuildMcpTextToolResult(
                        idEl,
                        CustomWidgetCapabilityRegistry.GenerateApiReferenceMarkdown(isChinese: false),
                        isError: false);
                }

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

                var sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(capId))
                {
                    IReadOnlyList<CustomWidgetCapabilitySpec> expanded =
                        CustomWidgetCapabilityRegistry.ExpandWithDependencies([capId.Trim()]);
                    if (expanded.Count == 0)
                    {
                        return BuildMcpTextToolResult(
                            idEl,
                            $"Capability '{capId}' not found. Call deskbox_get_capability_spec with no arguments to list all capability IDs.",
                            isError: true);
                    }

                    sb.AppendLine(CustomWidgetCapabilityRegistry.FormatSelectedCapsulesPromptBlock(expanded, []));
                    return BuildMcpTextToolResult(idEl, sb.ToString(), isError: false);
                }

                if (!string.IsNullOrWhiteSpace(query))
                {
                    IReadOnlyList<CustomWidgetCapabilitySpec> matches = CustomWidgetCapabilityRegistry.Search(query).Take(5).ToList();
                    if (matches.Count == 0)
                    {
                        sb.AppendLine($"No capability matched '{query}'. Showing Tier-0 capability directory:");
                        sb.AppendLine();
                        sb.AppendLine(CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock());
                        return BuildMcpTextToolResult(idEl, sb.ToString(), isError: false);
                    }

                    IReadOnlyList<CustomWidgetCapabilitySpec> withDeps =
                        CustomWidgetCapabilityRegistry.ExpandWithDependencies(matches.Select(static m => m.Id));
                    sb.AppendLine(CustomWidgetCapabilityRegistry.FormatSelectedCapsulesPromptBlock([], withDeps));
                    return BuildMcpTextToolResult(idEl, sb.ToString(), isError: false);
                }

                sb.AppendLine(CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock());
                sb.AppendLine();
                sb.AppendLine("## Available Capability Capsules (pass `capabilityId` or `query` for full contract + snippet)");
                foreach (CustomWidgetCapabilitySpec spec in CustomWidgetCapabilityRegistry.All)
                {
                    sb.Append("- `").Append(spec.Id).Append("` (").Append(spec.Emoji).Append(' ').Append(spec.TitleEn)
                      .Append("): ").AppendLine(spec.SummaryEn);
                }

                return BuildMcpTextToolResult(idEl, sb.ToString(), isError: false);
            }

            case "deskbox_validate_manifest":
            {
                string manifestJson = argsEl.ValueKind == JsonValueKind.Object &&
                                      argsEl.TryGetProperty("manifestJson", out JsonElement mj) &&
                                      mj.ValueKind == JsonValueKind.String
                    ? mj.GetString() ?? string.Empty
                    : string.Empty;
                CustomWidgetParseResult validation = CustomWidgetManifestParser.ValidateWithRuntimeHints(manifestJson);
                bool isValid = validation.Manifest is not null && !validation.Diagnostics.Any(static d => d.IsError);

                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    w.WriteStartObject();
                    w.WriteBoolean("isValid", isValid);
                    if (validation.Manifest is not null)
                    {
                        w.WriteString("id", validation.Manifest.Id);
                        w.WriteString("name", validation.Manifest.Name);
                    }
                    w.WriteStartArray("diagnostics");
                    foreach (CustomWidgetValidationDiagnostic d in validation.Diagnostics)
                    {
                        w.WriteStartObject();
                        w.WriteString("path", d.Path);
                        w.WriteString("severity", d.IsError ? "error" : "warning");
                        w.WriteString("message", d.Message);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                return BuildMcpTextToolResult(idEl, Encoding.UTF8.GetString(ms.ToArray()), isError: !isValid);
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

                    string? offlineSingle = BuildOfflineDiskSingleWidgetJson(trimmedId);
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

                // Offline disk fallback when DeskBox GUI is not running
                string offlineList = BuildOfflineDiskWidgetsListJson();
                return BuildMcpTextToolResult(idEl, offlineList, isError: false);
            }

            case "deskbox_upsert_widget":
            {
                string manifestJson = argsEl.ValueKind == JsonValueKind.Object &&
                                      argsEl.TryGetProperty("manifestJson", out JsonElement mj) &&
                                      mj.ValueKind == JsonValueKind.String
                    ? mj.GetString() ?? string.Empty
                    : string.Empty;
                string? widgetId = argsEl.ValueKind == JsonValueKind.Object &&
                                   argsEl.TryGetProperty("widgetId", out JsonElement wid) &&
                                   wid.ValueKind == JsonValueKind.String
                    ? wid.GetString()
                    : null;
                bool resize = argsEl.ValueKind == JsonValueKind.Object &&
                              argsEl.TryGetProperty("resizeToDefault", out JsonElement rz) &&
                              rz.ValueKind == JsonValueKind.True;

                string? bridgeResp = await SendToBridgeEnsuringAppAsync(
                    w =>
                    {
                        w.WriteString("op", "upsert");
                        if (!string.IsNullOrWhiteSpace(widgetId))
                        {
                            w.WriteString("widgetId", widgetId);
                        }
                        w.WriteString("manifestJson", manifestJson);
                        w.WriteBoolean("resizeToDefault", resize);
                        w.WriteBoolean("reveal", true);
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

                JsonElement resObj = rRoot.GetProperty("result");
                string base64Png = resObj.GetProperty("base64Png").GetString() ?? string.Empty;
                int width = resObj.TryGetProperty("width", out JsonElement wEl) ? wEl.GetInt32() : 0;
                int height = resObj.TryGetProperty("height", out JsonElement hEl) ? hEl.GetInt32() : 0;
                string resolvedMode = resObj.TryGetProperty("mode", out JsonElement mOutEl) && mOutEl.ValueKind == JsonValueKind.String
                    ? mOutEl.GetString() ?? "expanded"
                    : "expanded";

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
                    writer.WriteString("text", $"Captured native WinUI 3 preview for '{widgetId}' ({width}x{height}px, mode={resolvedMode}).");
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
            string exePath = CustomWidgetAgentConnectorService.ResolveDeskBoxExecutablePath();
            if (File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--silent",
                    UseShellExecute = false
                });

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

    private static string BuildOfflineDiskWidgetsListJson()
    {
        string root = CustomWidgetDataStore.GetWidgetsRootDirectory();
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("status", "offline-disk-scan");
            w.WriteString("widgetsRoot", root);
            w.WriteStartArray("widgets");
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
                        m = CustomWidgetManifestParser.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)).Manifest;
                    }
                    catch
                    {
                    }

                    w.WriteStartObject();
                    w.WriteString("widgetId", folderName);
                    w.WriteString("name", m?.Name ?? folderName);
                    w.WriteString("manifestId", m?.Id ?? string.Empty);
                    w.WriteString("icon", m?.Icon ?? string.Empty);
                    w.WriteString("directory", dir);
                    w.WriteString("manifestPath", manifestPath);
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string? BuildOfflineDiskSingleWidgetJson(string widgetIdOrManifestId)
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
            CustomWidgetManifest? m = null;
            try
            {
                rawJson = File.ReadAllText(manifestPath, Encoding.UTF8);
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

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteString("status", "offline-disk-scan");
                w.WriteString("widgetId", folderName);
                w.WriteString("name", m?.Name ?? folderName);
                w.WriteBoolean("liveLoaded", false);
                w.WriteString("displayMode", "offline");
                w.WriteString("directory", dir);
                w.WriteString("manifestPath", manifestPath);
                w.WriteString("manifestJson", rawJson);
                w.WriteEndObject();
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }

        return null;
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

            case "schema":
                Console.WriteLine(CustomWidgetTemplates.ManifestJsonSchema);
                return 0;

            case "list":
            {
                string? resp = await DeskBoxAgentBridgeClient.TrySendAsync(
                    w => w.WriteString("op", "list"),
                    pipeNameOverride);
                Console.WriteLine(resp ?? BuildOfflineDiskWidgetsListJson());
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
                    Console.Error.WriteLine("DeskBox is not currently running.");
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

                CustomWidgetParseResult res = CustomWidgetManifestParser.ValidateWithRuntimeHints(rawJson);
                bool valid = res.Manifest is not null && !res.Diagnostics.Any(static d => d.IsError);
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    w.WriteStartObject();
                    w.WriteBoolean("isValid", valid);
                    if (res.Manifest is not null)
                    {
                        w.WriteString("id", res.Manifest.Id);
                        w.WriteString("name", res.Manifest.Name);
                    }
                    w.WriteStartArray("diagnostics");
                    foreach (CustomWidgetValidationDiagnostic d in res.Diagnostics)
                    {
                        w.WriteStartObject();
                        w.WriteString("path", d.Path);
                        w.WriteString("severity", d.IsError ? "error" : "warning");
                        w.WriteString("message", d.Message);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                Console.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
                return valid ? 0 : 1;
            }

            case "create":
            case "update":
            case "upsert":
            case "apply":
            {
                string? id = GetFlagValue(args, "--id");
                string? file = GetFlagValue(args, "--file");
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                {
                    Console.Error.WriteLine($"Usage: DeskBox.exe widget {sub} [--id <widgetId>] --file <manifest.json>");
                    return 2;
                }

                string rawJson = await File.ReadAllTextAsync(file, Encoding.UTF8);
                bool resize = args.Any(static a => string.Equals(a, "--resize", StringComparison.OrdinalIgnoreCase));
                string? resp = await SendToBridgeEnsuringAppAsync(
                    w =>
                    {
                        w.WriteString("op", "upsert");
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            w.WriteString("widgetId", id);
                        }
                        w.WriteString("manifestJson", rawJson);
                        w.WriteBoolean("resizeToDefault", resize);
                        w.WriteBoolean("reveal", true);
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
                string? outPath = GetFlagValue(args, "--out");
                string? theme = GetFlagValue(args, "--theme");
                string? mode = GetFlagValue(args, "--mode");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(outPath))
                {
                    Console.Error.WriteLine("Usage: DeskBox.exe widget preview --id <widgetId> --out <preview.png> [--theme dark|light] [--mode expanded|capsule|live]");
                    return 2;
                }

                string? resp = await SendToBridgeEnsuringAppAsync(
                    w =>
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
                string b64 = doc.RootElement.GetProperty("result").GetProperty("base64Png").GetString() ?? string.Empty;
                byte[] pngBytes = Convert.FromBase64String(b64);
                string fullOut = Path.GetFullPath(outPath);
                string? parent = Path.GetDirectoryName(fullOut);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                await File.WriteAllBytesAsync(fullOut, pngBytes);
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
                            foreach (string pair in spec[(colon + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
                            {
                                int eq = pair.IndexOf('=');
                                if (eq > 0)
                                {
                                    record[pair[..eq].Trim()] = pair[(eq + 1)..];
                                }
                            }
                            if (record.Count == 0)
                            {
                                int eq = spec[(colon + 1)..].IndexOf('=');
                                if (eq < 0)
                                {
                                    record["text"] = spec[(colon + 1)..];
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

                stateDict.TryAdd("updatedAt", DateTime.Now.ToString("HH:mm:ss"));

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
                                w.WriteString(k, v);
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
                                    w.WriteString(fk, fv);
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
  DeskBox.exe widget schema                                Print manifest.schema.json
  DeskBox.exe widget list                                  List Custom Widgets
  DeskBox.exe widget get --id <id>                         Inspect widget manifest & state
  DeskBox.exe widget validate --file <manifest.json>       Validate manifest with runtime hints
  DeskBox.exe widget create --file <manifest.json>         Create live desktop widget
  DeskBox.exe widget update --id <id> --file <manifest>    Update live desktop widget
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
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
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
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildJsonRpcError(JsonElement? idEl, int code, string message)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
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
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
