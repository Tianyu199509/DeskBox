using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Per-user, session-scoped Named Pipe bridge allowing external AI Agents (<c>DeskBox.exe --mcp</c>
/// and <c>DeskBox.exe widget ...</c> CLI) to inspect, validate, create/hot-reload, visually capture
/// PNG previews, and push live telemetry to DeskBox Custom Widgets.
/// </summary>
public sealed class DeskBoxAgentBridgeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<WidgetManager?> _widgetManagerProvider;
    private readonly bool _manageHttpFromPreferences;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workerTasks = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _httpGate = new(1, 1);
    private DeskBoxAgentHttpBridgeServer? _httpServer;
    private string? _httpLastError;
    private Task? _acceptLoop;
    private volatile bool _disposed;

    public DeskBoxAgentBridgeServer(
        string pipeName,
        Func<WidgetManager?> widgetManagerProvider,
        bool manageHttpFromPreferences = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(widgetManagerProvider);
        _pipeName = pipeName;
        _widgetManagerProvider = widgetManagerProvider;
        _manageHttpFromPreferences = manageHttpFromPreferences;
    }

    public string PipeName => _pipeName;

    public DeskBoxAgentHttpBridgeServer? HttpServer => _httpServer;

    public void Start()
    {
        if (_acceptLoop is not null || _disposed)
        {
            return;
        }

        _acceptLoop = Task.Run(AcceptLoopAsync);
        if (_manageHttpFromPreferences && CustomWidgetStudioPreferencesStore.Shared.AgentHttpEnabled)
        {
            _ = SyncHttpServerFromPreferencesAsync();
        }
    }

    public async Task<(bool Running, int Port, string McpUrl, string PushUrl, string? Error)> ConfigureHttpServerAsync(
        bool enabled,
        int port,
        Func<string>? tokenProvider = null)
    {
        if (_disposed)
        {
            return (false, 0, string.Empty, string.Empty, "Bridge server is disposed.");
        }

        await _httpGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!enabled)
            {
                if (_httpServer is { } existing)
                {
                    _httpServer = null;
                    await existing.DisposeAsync().ConfigureAwait(false);
                }
                _httpLastError = null;
                return (false, port, string.Empty, string.Empty, null);
            }

            if (_httpServer is { IsRunning: true } running && running.ActualPort == port)
            {
                return (true, running.ActualPort, running.McpEndpointUrl, running.RestPushUrl, null);
            }

            if (_httpServer is { } oldServer)
            {
                _httpServer = null;
                await oldServer.DisposeAsync().ConfigureAwait(false);
            }

            Func<string> effectiveTokenProvider = tokenProvider ??
                (() => CustomWidgetStudioPreferencesStore.Shared.GetOrCreateAgentHttpToken());
            var next = new DeskBoxAgentHttpBridgeServer(port, effectiveTokenProvider, DispatchRequestJsonAsync);
            if (next.TryStart(out string? err))
            {
                _httpServer = next;
                _httpLastError = null;
                return (true, next.ActualPort, next.McpEndpointUrl, next.RestPushUrl, null);
            }

            _httpLastError = err;
            await next.DisposeAsync().ConfigureAwait(false);
            return (false, port, string.Empty, string.Empty, err);
        }
        finally
        {
            _httpGate.Release();
        }
    }

    public Task<(bool Running, int Port, string McpUrl, string PushUrl, string? Error)> SyncHttpServerFromPreferencesAsync()
    {
        CustomWidgetStudioPreferencesStore prefs = CustomWidgetStudioPreferencesStore.Shared;
        return ConfigureHttpServerAsync(
            prefs.AgentHttpEnabled,
            prefs.AgentHttpPort,
            () => prefs.GetOrCreateAgentHttpToken());
    }

    private static PipeSecurity? TryCreateCurrentUserPipeSecurity()
    {
        try
        {
            SecurityIdentifier? sid = WindowsIdentity.GetCurrent().User;
            if (sid is null)
            {
                return null;
            }

            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                sid,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
            return security;
        }
        catch
        {
            return null;
        }
    }

    private async Task AcceptLoopAsync()
    {
        PipeSecurity? security = TryCreateCurrentUserPipeSecurity();
        if (security is null)
        {
            App.Log("[AgentBridge] Refusing to start named-pipe server without current-user ACL.");
            return;
        }

        while (!_cts.IsCancellationRequested && !_disposed)
        {
            NamedPipeServerStream? serverStream = null;
            try
            {
                serverStream = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    16,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    64 * 1024,
                    64 * 1024,
                    security);

                await serverStream.WaitForConnectionAsync(_cts.Token);
                NamedPipeServerStream connected = serverStream;
                serverStream = null;

                Task clientTask = Task.Run(() => HandleClientAsync(connected, _cts.Token));
                lock (_gate)
                {
                    _workerTasks.RemoveAll(static t => t.IsCompleted);
                    _workerTasks.Add(clientTask);
                }
            }
            catch (OperationCanceledException)
            {
                serverStream?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                serverStream?.Dispose();
                App.Log($"[AgentBridge] Pipe accept error: {ex.Message}");
                try
                {
                    await Task.Delay(300, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        await using (stream)
        {
            try
            {
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 16 * 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                while (!cancellationToken.IsCancellationRequested && stream.IsConnected)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string responseJson = await DispatchRequestJsonAsync(line, cancellationToken);
                    await writer.WriteLineAsync(responseJson.AsMemory(), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (Exception ex)
            {
                App.Log($"[AgentBridge] Client session error: {ex.Message}");
            }
        }
    }

    public async Task<string> DispatchRequestJsonAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(requestJson);
            JsonElement root = doc.RootElement;
            string op = root.TryGetProperty("op", out JsonElement opEl) && opEl.ValueKind == JsonValueKind.String
                ? opEl.GetString() ?? string.Empty
                : string.Empty;

            return op.ToLowerInvariant() switch
            {
                "ping" => BuildSuccessJson(writer =>
                {
                    DeskBoxAgentHttpBridgeServer? http = _httpServer;
                    writer.WriteString("status", "online");
                    writer.WriteNumber("pid", Environment.ProcessId);
                    writer.WriteString("widgetsRoot", CustomWidgetDataStore.GetWidgetsRootDirectory());
                    writer.WriteNumber("toolsCount", DeskBoxAgentCliEntry.GetMcpToolSummaries().Count);
                    writer.WriteNumber("skillVersion", CustomWidgetRemotePromptService.Shared.GetEffectivePromptPack().PromptVersion);
                    writer.WriteBoolean("httpRunning", http is { IsRunning: true });
                    writer.WriteNumber("httpPort", http is { IsRunning: true } ? http.ActualPort : CustomWidgetStudioPreferencesStore.Shared.AgentHttpPort);
                    writer.WriteString("httpMcpUrl", http is { IsRunning: true } ? http.McpEndpointUrl : string.Empty);
                    writer.WriteString("httpPushUrl", http is { IsRunning: true } ? http.RestPushUrl : string.Empty);
                    if (!string.IsNullOrWhiteSpace(_httpLastError))
                    {
                        writer.WriteString("httpError", _httpLastError);
                    }
                }),

                "http_status" => BuildSuccessJson(writer =>
                {
                    DeskBoxAgentHttpBridgeServer? http = _httpServer;
                    writer.WriteBoolean("enabled", CustomWidgetStudioPreferencesStore.Shared.AgentHttpEnabled);
                    writer.WriteBoolean("running", http is { IsRunning: true });
                    writer.WriteNumber("port", http is { IsRunning: true } ? http.ActualPort : CustomWidgetStudioPreferencesStore.Shared.AgentHttpPort);
                    writer.WriteString("mcpUrl", http is { IsRunning: true } ? http.McpEndpointUrl : string.Empty);
                    writer.WriteString("pushUrl", http is { IsRunning: true } ? http.RestPushUrl : string.Empty);
                    if (!string.IsNullOrWhiteSpace(_httpLastError))
                    {
                        writer.WriteString("error", _httpLastError);
                    }
                }),

                "http_configure" => await HandleHttpConfigureAsync(root),

                "spec" => BuildSuccessJson(writer =>
                {
                    writer.WriteString("widgetsRoot", CustomWidgetDataStore.GetWidgetsRootDirectory());
                    writer.WriteString("skillMarkdown", CustomWidgetTemplates.AiAgentSkillMarkdown);
                    writer.WriteString("manifestJsonSchema", CustomWidgetTemplates.ManifestJsonSchema);
                }),

                "validate" => HandleValidate(root),
                "list" => await HandleListAsync(),
                "get" => await HandleGetAsync(root),
                "upsert" => await HandleUpsertAsync(root),
                "push_data" => await HandlePushDataAsync(root),
                "capture_preview" => await HandleCapturePreviewAsync(root),
                _ => BuildErrorJson($"Unsupported operation '{op}'.")
            };
        }
        catch (Exception ex)
        {
            return BuildErrorJson(ex.Message);
        }
    }

    private async Task<string> HandleHttpConfigureAsync(JsonElement root)
    {
        CustomWidgetStudioPreferencesStore prefs = CustomWidgetStudioPreferencesStore.Shared;
        bool enabled = root.TryGetProperty("enabled", out JsonElement enEl) &&
                       enEl.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? enEl.GetBoolean()
            : prefs.AgentHttpEnabled;
        int port = root.TryGetProperty("port", out JsonElement portEl) &&
                   portEl.ValueKind == JsonValueKind.Number &&
                   portEl.TryGetInt32(out int parsedPort)
            ? parsedPort
            : prefs.AgentHttpPort;

        var (running, boundPort, mcpUrl, pushUrl, err) = await ConfigureHttpServerAsync(
            enabled,
            port,
            () => prefs.GetOrCreateAgentHttpToken());

        return BuildSuccessJson(writer =>
        {
            writer.WriteBoolean("enabled", enabled);
            writer.WriteBoolean("running", running);
            writer.WriteNumber("port", boundPort);
            writer.WriteString("mcpUrl", mcpUrl);
            writer.WriteString("pushUrl", pushUrl);
            if (!string.IsNullOrWhiteSpace(err))
            {
                writer.WriteString("error", err);
            }
        });
    }

    private static string HandleValidate(JsonElement root)
    {
        string manifestJson = root.TryGetProperty("manifestJson", out JsonElement jEl) && jEl.ValueKind == JsonValueKind.String
            ? jEl.GetString() ?? string.Empty
            : string.Empty;
        CustomWidgetParseResult result = CustomWidgetManifestParser.ValidateWithRuntimeHints(manifestJson);

        return BuildSuccessJson(writer =>
        {
            bool hasErrors = result.Diagnostics.Any(static d => d.IsError);
            writer.WriteBoolean("isValid", result.Manifest is not null && !hasErrors);
            if (result.Manifest is not null)
            {
                writer.WriteString("id", result.Manifest.Id);
                writer.WriteString("name", result.Manifest.Name);
            }

            writer.WriteStartArray("diagnostics");
            foreach (CustomWidgetValidationDiagnostic diag in result.Diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("path", diag.Path);
                writer.WriteString("message", diag.Message);
                writer.WriteString("severity", diag.IsError ? "error" : "warning");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
    }

    private async Task<string> HandleListAsync()
    {
        WidgetManager? manager = _widgetManagerProvider();
        if (manager is null)
        {
            return BuildErrorJson("DeskBox WidgetManager is still initializing.");
        }

        IReadOnlyList<GlanceWidgetInstanceInfo> instances = manager.GetCustomWidgetInstances();
        var items = new List<(GlanceWidgetInstanceInfo Info, CustomWidgetManifest? Manifest, string Dir, string ManifestPath)>();

        foreach (GlanceWidgetInstanceInfo inst in instances)
        {
            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(inst.Id);
            CustomWidgetManifest? parsedManifest = null;
            if (File.Exists(store.ManifestFilePath))
            {
                try
                {
                    string raw = await File.ReadAllTextAsync(store.ManifestFilePath, Encoding.UTF8);
                    parsedManifest = CustomWidgetManifestParser.Parse(raw).Manifest;
                }
                catch
                {
                }
            }
            items.Add((inst, parsedManifest, store.WidgetDirectory, store.ManifestFilePath));
        }

        return BuildSuccessJson(writer =>
        {
            writer.WriteString("widgetsRoot", CustomWidgetDataStore.GetWidgetsRootDirectory());
            writer.WriteStartArray("widgets");
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("widgetId", item.Info.Id);
                writer.WriteString("name", item.Info.Name);
                writer.WriteBoolean("enabled", item.Info.IsEnabled);
                writer.WriteString("manifestId", item.Manifest?.Id ?? string.Empty);
                writer.WriteString("icon", item.Manifest?.Icon ?? string.Empty);
                writer.WriteString("description", item.Manifest?.Description ?? string.Empty);
                writer.WriteString("directory", item.Dir);
                writer.WriteString("manifestPath", item.ManifestPath);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
    }

    private async Task<string> HandleGetAsync(JsonElement root)
    {
        WidgetManager? manager = _widgetManagerProvider();
        if (manager is null)
        {
            return BuildErrorJson("DeskBox WidgetManager is still initializing.");
        }

        string widgetId = root.TryGetProperty("widgetId", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? string.Empty
            : string.Empty;

        WidgetConfig? config = manager.ResolveCustomWidgetConfig(widgetId);
        if (config is null)
        {
            return BuildErrorJson($"Custom widget '{widgetId}' not found.");
        }

        CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(config.Id);
        string manifestJson = await store.ReadOrCreateManifestJsonAsync();
        CustomWidgetLiveRuntimeSnapshot? liveSnapshot = await manager.TryGetCustomWidgetLiveSnapshotAsync(config.Id);
        CustomWidgetStorePayload? diskPayload = liveSnapshot is null ? await store.LoadAsync() : null;

        return BuildSuccessJson(writer =>
        {
            writer.WriteString("widgetId", config.Id);
            writer.WriteString("name", config.Name);
            writer.WriteBoolean("visible", config.IsVisible && !config.IsDisabled);
            writer.WriteBoolean("liveLoaded", liveSnapshot?.LiveLoaded == true);
            writer.WriteString("displayMode", liveSnapshot?.DisplayMode ?? "offline");
            writer.WriteString("directory", store.WidgetDirectory);
            writer.WriteString("manifestPath", store.ManifestFilePath);
            writer.WriteString("manifestJson", manifestJson);

            writer.WriteStartObject("state");
            if (liveSnapshot is not null)
            {
                foreach ((string k, string v) in liveSnapshot.State)
                {
                    writer.WriteString(k, v);
                }
            }
            else if (diskPayload is not null)
            {
                foreach ((string k, string v) in diskPayload.State)
                {
                    writer.WriteString(k, v);
                }
            }
            writer.WriteEndObject();

            writer.WriteStartObject("collectionCounts");
            if (liveSnapshot is not null)
            {
                foreach ((string col, int count) in liveSnapshot.CollectionCounts)
                {
                    writer.WriteNumber(col, count);
                }
            }
            else if (diskPayload is not null)
            {
                foreach ((string col, List<CustomWidgetRecord> list) in diskPayload.Collections)
                {
                    writer.WriteNumber(col, list.Count);
                }
            }
            writer.WriteEndObject();

            writer.WriteStartObject("sampleItems");
            if (liveSnapshot is not null)
            {
                foreach ((string col, List<Dictionary<string, string>> sampleList) in liveSnapshot.SampleItems)
                {
                    writer.WriteStartArray(col);
                    foreach (Dictionary<string, string> sample in sampleList)
                    {
                        writer.WriteStartObject();
                        foreach ((string fk, string fv) in sample)
                        {
                            writer.WriteString(fk, fv);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
            }
            else if (diskPayload is not null)
            {
                foreach ((string col, List<CustomWidgetRecord> list) in diskPayload.Collections)
                {
                    writer.WriteStartArray(col);
                    for (int i = 0; i < list.Count && i < 3; i++)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", list[i].Id);
                        foreach ((string fk, string fv) in list[i].Fields)
                        {
                            writer.WriteString(fk, fv);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
            }
            writer.WriteEndObject();
        });
    }

    private async Task<string> HandleUpsertAsync(JsonElement root)
    {
        WidgetManager? manager = _widgetManagerProvider();
        if (manager is null)
        {
            return BuildErrorJson("DeskBox WidgetManager is still initializing.");
        }

        string? widgetId = root.TryGetProperty("widgetId", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;
        string manifestJson = root.TryGetProperty("manifestJson", out JsonElement mEl) && mEl.ValueKind == JsonValueKind.String
            ? mEl.GetString() ?? string.Empty
            : string.Empty;
        bool resizeToDefault = root.TryGetProperty("resizeToDefault", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.True;
        bool reveal = !root.TryGetProperty("reveal", out JsonElement revEl) || revEl.ValueKind != JsonValueKind.False;

        CustomWidgetParseResult validation = CustomWidgetManifestParser.ValidateWithRuntimeHints(manifestJson);
        if (validation.Manifest is null || validation.Diagnostics.Any(static d => d.IsError))
        {
            string firstErr = validation.Diagnostics.FirstOrDefault(static d => d.IsError)?.Message ?? "Invalid manifest.json";
            return BuildErrorJson(firstErr);
        }

        GlanceWidgetInstanceInfo info = await manager.UpsertCustomWidgetFromAgentAsync(
            widgetId,
            manifestJson,
            resizeToDefault,
            reveal);
        CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(info.Id);

        return BuildSuccessJson(writer =>
        {
            writer.WriteString("widgetId", info.Id);
            writer.WriteString("name", info.Name);
            writer.WriteBoolean("enabled", info.IsEnabled);
            writer.WriteString("directory", store.WidgetDirectory);
            writer.WriteString("manifestPath", store.ManifestFilePath);
            writer.WriteStartArray("warnings");
            foreach (CustomWidgetValidationDiagnostic warn in validation.Diagnostics.Where(static d => !d.IsError))
            {
                writer.WriteStringValue($"{warn.Path}: {warn.Message}");
            }
            writer.WriteEndArray();
        });
    }

    private async Task<string> HandlePushDataAsync(JsonElement root)
    {
        WidgetManager? manager = _widgetManagerProvider();
        if (manager is null)
        {
            return BuildErrorJson("DeskBox WidgetManager is still initializing.");
        }

        string widgetId = root.TryGetProperty("widgetId", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            widgetId = CustomWidgetTemplates.AgentHudTemplateId;
        }

        Dictionary<string, string>? stateUpdates = null;
        if (root.TryGetProperty("state", out JsonElement stateEl) && stateEl.ValueKind == JsonValueKind.Object)
        {
            stateUpdates = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty prop in stateEl.EnumerateObject())
            {
                stateUpdates[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.GetRawText();
            }
        }

        string? collectionName = root.TryGetProperty("collection", out JsonElement colEl) && colEl.ValueKind == JsonValueKind.String
            ? colEl.GetString()
            : null;
        string? mode = root.TryGetProperty("mode", out JsonElement modeEl) && modeEl.ValueKind == JsonValueKind.String
            ? modeEl.GetString()
            : null;

        List<Dictionary<string, string>>? records = null;
        if (root.TryGetProperty("items", out JsonElement itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            records = [];
            foreach (JsonElement recEl in itemsEl.EnumerateArray())
            {
                if (recEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (JsonProperty fProp in recEl.EnumerateObject())
                {
                    dict[fProp.Name] = fProp.Value.ValueKind == JsonValueKind.String
                        ? fProp.Value.GetString() ?? string.Empty
                        : fProp.Value.GetRawText();
                }
                records.Add(dict);
            }
        }

        var (ok, resolvedId, err) = await manager.PushCustomWidgetDataAsync(
            widgetId,
            stateUpdates,
            collectionName,
            mode,
            records);

        if (!ok)
        {
            return BuildErrorJson(err ?? "Push failed.");
        }

        return BuildSuccessJson(writer =>
        {
            writer.WriteString("widgetId", resolvedId ?? widgetId);
            writer.WriteBoolean("pushed", true);
        });
    }

    private async Task<string> HandleCapturePreviewAsync(JsonElement root)
    {
        WidgetManager? manager = _widgetManagerProvider();
        if (manager is null)
        {
            return BuildErrorJson("DeskBox WidgetManager is still initializing.");
        }

        string widgetId = root.TryGetProperty("widgetId", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? string.Empty
            : string.Empty;
        string? theme = root.TryGetProperty("theme", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.String
            ? tEl.GetString()
            : null;
        string? mode = root.TryGetProperty("mode", out JsonElement mEl) && mEl.ValueKind == JsonValueKind.String
            ? mEl.GetString()
            : null;
        string effectiveMode = (mode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "capsule" => "capsule",
            "live" => "live",
            _ => "expanded"
        };

        var (pngBytes, width, height, err) = await manager.CaptureCustomWidgetPreviewPngAsync(widgetId, theme, effectiveMode);
        if (pngBytes is null || pngBytes.Length == 0)
        {
            return BuildErrorJson(err ?? "Failed to capture widget preview.");
        }

        return BuildSuccessJson(writer =>
        {
            writer.WriteString("widgetId", widgetId);
            writer.WriteString("mode", effectiveMode);
            writer.WriteNumber("width", width);
            writer.WriteNumber("height", height);
            writer.WriteString("mimeType", "image/png");
            writer.WriteString("base64Png", Convert.ToBase64String(pngBytes));
        });
    }

    internal static string BuildSuccessJson(Action<Utf8JsonWriter> writeResultObject)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writeResultObject(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static string BuildErrorJson(string message)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", false);
            writer.WriteString("error", message);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_httpServer is { } http)
        {
            _httpServer = null;
            try
            {
                await http.DisposeAsync();
            }
            catch
            {
            }
        }

        try
        {
            await _cts.CancelAsync();
        }
        catch
        {
        }

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
            pending = _workerTasks.ToArray();
            _workerTasks.Clear();
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
        _httpGate.Dispose();
    }
}

public static class DeskBoxAgentBridgeClient
{
    public static async Task<string?> TrySendAsync(
        Action<Utf8JsonWriter> writePayload,
        string? pipeName = null,
        int connectTimeoutMs = 1500,
        CancellationToken cancellationToken = default)
    {
        string targetPipe = string.IsNullOrWhiteSpace(pipeName)
            ? DeskBoxDataPathService.Current.AgentBridgePipeName
            : pipeName;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writePayload(writer);
            writer.WriteEndObject();
        }

        string requestLine = Encoding.UTF8.GetString(ms.ToArray());

        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                targetPipe,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(connectTimeoutMs, cancellationToken);

            await using var streamWriter = new StreamWriter(
                client,
                new UTF8Encoding(false),
                bufferSize: 16 * 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            using var streamReader = new StreamReader(
                client,
                new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 16 * 1024,
                leaveOpen: true);

            await streamWriter.WriteLineAsync(requestLine.AsMemory(), cancellationToken);
            return await streamReader.ReadLineAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}
