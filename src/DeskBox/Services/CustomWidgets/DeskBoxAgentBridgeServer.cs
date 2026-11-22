using System.Buffers;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.FileSafety;
using DeskBox.Models;
using DeskBox.Services.Ipc;

namespace DeskBox.Services;

/// <summary>
/// Host-side approval request for external commits that overwrite an existing widget,
/// clear its data, or declare privileged capabilities. "User confirmation" in the tool
/// response is prompt text only; the approval provider is what actually enforces it.
/// </summary>
public sealed record AgentCommitApprovalRequest(
    string WidgetId,
    bool OverwritesExisting,
    bool ClearsExistingData,
    bool DeclaresPrivilegedCapabilities,
    string? WidgetName = null);

/// <summary>
/// Per-user, session-scoped Named Pipe bridge allowing external AI Agents (<c>DeskBox.exe --mcp</c>
/// and <c>DeskBox.exe widget ...</c> CLI) to inspect, validate, stage-then-commit, visually capture
/// PNG previews, and push live telemetry to DeskBox Custom Widgets. External writes are staged by
/// default; only an explicit commit (or the Workshop's user confirmation) changes the desktop.
/// </summary>
public sealed class DeskBoxAgentBridgeServer : IAsyncDisposable
{
    private const int MaxConcurrentClients = 16;
    private static readonly TimeSpan ClientIdleTimeout = TimeSpan.FromMinutes(15);

    private readonly string _pipeName;
    private readonly Func<WidgetManager?> _widgetManagerProvider;
    private readonly Func<bool, Task<string>>? _studioContextProvider;
    private readonly Func<(int Width, int Height)>? _hostDefaultSizeProvider;
    private readonly Func<string?, bool, TimeSpan, Task<(byte[] PngBytes, int Width, int Height, int OverflowPx)?>>? _studioPreviewCapturer;
    private readonly Func<AgentCommitApprovalRequest, Task<bool>>? _commitApprovalProvider;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _connectionSlots = new(MaxConcurrentClients, MaxConcurrentClients);
    private readonly List<Task> _workerTasks = [];
    private readonly object _gate = new();
    private Task? _acceptLoop;
    private volatile bool _disposed;
    private volatile ConnectedMcpClientInfo? _lastConnectedMcpClient;
    private static volatile ConnectedMcpClientInfo? s_lastConnectedMcpClient;
    private static volatile DeskBoxAgentBridgeServer? s_activeInstance;

    public static event Action<ConnectedMcpClientInfo>? McpClientConnected;

    public static event Action<string, string>? McpActivityRecorded;

    public static ConnectedMcpClientInfo? LastMcpClient => s_lastConnectedMcpClient;

    public ConnectedMcpClientInfo? LastConnectedMcpClient => _lastConnectedMcpClient;

    internal static WidgetManager? TryGetActiveWidgetManager() =>
        s_activeInstance?._widgetManagerProvider();

    internal static void ClearLastMcpClientForTests() => s_lastConnectedMcpClient = null;

    internal static void PublishExternalMcpClientConnected(ConnectedMcpClientInfo info)
    {
        s_lastConnectedMcpClient = info;
        McpClientConnected?.Invoke(info);
    }

    internal static void PublishExternalMcpActivity(string op, string detail)
    {
        McpActivityRecorded?.Invoke(op, detail);
    }

    public DeskBoxAgentBridgeServer(
        string pipeName,
        Func<WidgetManager?> widgetManagerProvider,
        Func<bool, Task<string>>? studioContextProvider = null,
        Func<(int Width, int Height)>? hostDefaultSizeProvider = null,
        Func<string?, bool, TimeSpan, Task<(byte[] PngBytes, int Width, int Height, int OverflowPx)?>>? studioPreviewCapturer = null,
        Func<AgentCommitApprovalRequest, Task<bool>>? commitApprovalProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(widgetManagerProvider);
        _pipeName = pipeName;
        _widgetManagerProvider = widgetManagerProvider;
        _studioContextProvider = studioContextProvider;
        _hostDefaultSizeProvider = hostDefaultSizeProvider;
        _studioPreviewCapturer = studioPreviewCapturer;
        _commitApprovalProvider = commitApprovalProvider;
    }

    public string PipeName => _pipeName;

    public void Start()
    {
        if (_acceptLoop is not null || _disposed)
        {
            return;
        }

        s_activeInstance = this;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private static bool HasCurrentUserSid()
    {
        try
        {
            using WindowsIdentity currentIdentity = WindowsIdentity.GetCurrent();
            return currentIdentity.User is not null;
        }
        catch
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        if (!HasCurrentUserSid())
        {
            App.Log("[AgentBridge] Refusing to start named-pipe server without current-user ACL.");
            return;
        }

        bool isFirstBind = true;
        while (!_cts.IsCancellationRequested && !_disposed)
        {
            bool slotAcquired = false;
            NamedPipeServerStream? serverStream = null;
            try
            {
                await _connectionSlots.WaitAsync(_cts.Token).ConfigureAwait(false);
                slotAcquired = true;

                serverStream = DeskBox.Platform.SingleInstanceAndActivationInterop.TryCreateMediumIntegrityCurrentUserPipeServer(
                    _pipeName,
                    maxInstances: MaxConcurrentClients,
                    bufferSize: 64 * 1024,
                    log: App.Log,
                    firstInstance: isFirstBind);
                if (serverStream is null)
                {
                    throw new InvalidOperationException("CreateNamedPipeW failed for agent bridge pipe.");
                }
                isFirstBind = false;

                await serverStream.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                NamedPipeServerStream connected = serverStream;
                serverStream = null;
                slotAcquired = false;

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
                if (slotAcquired)
                {
                    ReleaseConnectionSlotSafely();
                }

                break;
            }
            catch (Exception ex)
            {
                serverStream?.Dispose();
                if (slotAcquired)
                {
                    ReleaseConnectionSlotSafely();
                }

                App.Log($"[AgentBridge] Pipe accept error: {ex.Message}");
                try
                {
                    await Task.Delay(300, _cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        try
        {
            await using (stream)
            {
                try
                {
                    using var reader = new PipeBoundedLineReader(stream);
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 16 * 1024, leaveOpen: true)
                    {
                        AutoFlush = true,
                        NewLine = "\n"
                    };

                    const int MaxBridgeRequestChars = 16 * 1024 * 1024;
                    while (!cancellationToken.IsCancellationRequested && stream.IsConnected)
                    {
                        string? line = await reader.ReadLineAsync(
                            MaxBridgeRequestChars,
                            cancellationToken,
                            onOversizedLine: static _ =>
                                App.Log($"[AgentBridge] Dropping oversized request line exceeding {MaxBridgeRequestChars} chars"),
                            idleTimeout: ClientIdleTimeout).ConfigureAwait(false);
                        if (line is null)
                        {
                            break;
                        }

                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        string responseJson = await DispatchRequestJsonAsync(line, cancellationToken).ConfigureAwait(false);
                        await writer.WriteLineAsync(responseJson.AsMemory(), cancellationToken).ConfigureAwait(false);
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
        finally
        {
            ReleaseConnectionSlotSafely();
        }
    }

    private void ReleaseConnectionSlotSafely()
    {
        try
        {
            _connectionSlots.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public Task<string> DispatchRequestJsonAsync(string requestJson, CancellationToken cancellationToken = default) =>
        DispatchRequestJsonCoreAsync(requestJson, trustedHostUiInitiated: false, cancellationToken);

    internal Task<string> DispatchHostUiRequestJsonAsync(string requestJson, CancellationToken cancellationToken = default) =>
        DispatchRequestJsonCoreAsync(requestJson, trustedHostUiInitiated: true, cancellationToken);

    private async Task<string> DispatchRequestJsonCoreAsync(
        string requestJson,
        bool trustedHostUiInitiated,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(requestJson);
            JsonElement root = doc.RootElement;
            string op = root.TryGetProperty("op", out JsonElement opEl) && opEl.ValueKind == JsonValueKind.String
                ? opEl.GetString() ?? string.Empty
                : string.Empty;

            string normalizedOp = op.ToLowerInvariant();
            if (normalizedOp is "mcp_hello" or "studio_context" or "validate" or "list" or "get" or "upsert" or "stage_commit" or "push_data" or "capture_preview")
            {
                string activityOp = normalizedOp switch
                {
                    "get" => "list",
                    "push_data" => "push",
                    _ => normalizedOp
                };
                string detail = normalizedOp switch
                {
                    "mcp_hello" => root.TryGetProperty("clientName", out JsonElement cn) && cn.ValueKind == JsonValueKind.String
                        ? cn.GetString() ?? string.Empty
                        : string.Empty,
                    "upsert" => root.TryGetProperty("commit", out JsonElement cm) && cm.ValueKind == JsonValueKind.True
                        ? "commit=true"
                        : "stage",
                    _ => string.Empty
                };
                try
                {
                    McpActivityRecorded?.Invoke(activityOp, detail);
                }
                catch
                {
                }
            }

            return normalizedOp switch
            {
                "ping" => BuildSuccessJson(writer =>
                {
                    writer.WriteString("status", "online");
                    writer.WriteNumber("pid", Environment.ProcessId);
                    writer.WriteString("appVersion", CustomWidgetAgentConnectorService.GetAppVersionString());
                    writer.WriteNumber("schemaVersion", 1);
                    writer.WriteString("widgetsRoot", CustomWidgetDataStore.GetWidgetsRootDirectory());
                    writer.WriteNumber("toolsCount", DeskBoxAgentCliEntry.GetMcpToolSummaries().Count);
                    writer.WriteNumber("skillVersion", CustomWidgetRemotePromptService.Shared.GetEffectivePromptPack().PromptVersion);
                    if ((_lastConnectedMcpClient ?? s_lastConnectedMcpClient) is { } client)
                    {
                        writer.WriteString("lastMcpClientName", client.ClientName);
                        if (!string.IsNullOrWhiteSpace(client.ClientVersion))
                        {
                            writer.WriteString("lastMcpClientVersion", client.ClientVersion);
                        }
                        writer.WriteString("lastMcpConnectedAtUtc", client.ConnectedAtUtc.ToString("O"));
                    }
                }),

                "mcp_hello" => HandleMcpHello(root),
                "studio_context" => await HandleStudioContextAsync(root),
                "validate" => HandleValidate(root),
                "list" => await HandleListAsync(),
                "get" => await HandleGetAsync(root),
                "upsert" => await HandleUpsertAsync(root, trustedHostUiInitiated),
                "stage_list" => HandleStageList(),
                "stage_get" => HandleStageGet(root),
                "stage_commit" => await HandleStageCommitAsync(root, trustedHostUiInitiated),
                "stage_discard" => HandleStageDiscard(root),
                "push_data" => await HandlePushDataAsync(root),
                "capture_preview" => await HandleCapturePreviewAsync(root),
                "sync_icon" => await HandleSyncIconAsync(root),
                _ => BuildErrorJson($"Unsupported operation '{op}'.")
            };
        }
        catch (Exception ex)
        {
            return BuildErrorJson(ex.Message);
        }
    }

    private string HandleMcpHello(JsonElement root)
    {
        string clientName = root.TryGetProperty("clientName", out JsonElement cnEl) && cnEl.ValueKind == JsonValueKind.String
            ? cnEl.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        string? clientVersion = root.TryGetProperty("clientVersion", out JsonElement cvEl) && cvEl.ValueKind == JsonValueKind.String
            ? cvEl.GetString()?.Trim()
            : null;
        if (!string.IsNullOrWhiteSpace(clientName))
        {
            var info = new ConnectedMcpClientInfo(
                clientName,
                string.IsNullOrWhiteSpace(clientVersion) ? null : clientVersion,
                DateTimeOffset.UtcNow);
            _lastConnectedMcpClient = info;
            s_lastConnectedMcpClient = info;
            try
            {
                McpClientConnected?.Invoke(info);
            }
            catch
            {
            }
        }

        return BuildSuccessJson(writer => writer.WriteBoolean("recorded", !string.IsNullOrWhiteSpace(clientName)));
    }

    private static string HandleValidate(JsonElement root)
    {
        string manifestJson = root.TryGetProperty("manifestJson", out JsonElement mj) && mj.ValueKind == JsonValueKind.String
            ? mj.GetString() ?? string.Empty
            : string.Empty;
        string? logicJs = root.TryGetProperty("logicJs", out JsonElement lj) && lj.ValueKind == JsonValueKind.String
            ? lj.GetString()
            : null;
        CustomWidgetLocaleCatalog? locales = null;
        if (root.TryGetProperty("locales", out JsonElement locEl))
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

        string reportJson = BuildValidationReportJson(
            manifestJson,
            logicJs,
            locales,
            extraDiagnostics: null,
            indented: false,
            out _);
        return "{\"ok\":true,\"result\":" + reportJson + "}";
    }

    private async Task<string> HandleStudioContextAsync(JsonElement root)
    {
        bool includeManifest = !root.TryGetProperty("includeManifest", out JsonElement incEl) ||
                               incEl.ValueKind != JsonValueKind.False;
        if (_studioContextProvider is not null)
        {
            return await _studioContextProvider(includeManifest);
        }

        (int hostW, int hostH) = _hostDefaultSizeProvider?.Invoke() ??
            ((int)SettingsService.DefaultWidgetWidth, (int)SettingsService.DefaultWidgetHeight);
        return BuildHeadlessStudioContextResponseJson(
            _widgetManagerProvider()?.ActiveCultureName,
            hostW,
            hostH);
    }

    internal static string BuildHeadlessStudioContextResponseJson(
        string? activeCulture = null,
        int? defaultWidgetWidth = null,
        int? defaultWidgetHeight = null)
    {
        return BuildSuccessJson(writer =>
        {
            WriteCommonStudioContextHeader(
                writer,
                hostOnline: true,
                activeCulture,
                defaultWidgetWidth,
                defaultWidgetHeight);
            writer.WriteBoolean("isOpen", false);
        });
    }

    internal static void WriteCommonStudioContextHeader(
        Utf8JsonWriter writer,
        bool hostOnline,
        string? activeCulture,
        int? defaultWidgetWidth = null,
        int? defaultWidgetHeight = null)
    {
        string resolvedCulture = !string.IsNullOrWhiteSpace(activeCulture)
            ? activeCulture!
            : LocalizationService.ResolvePersistedOrAmbientCultureName();
        writer.WriteBoolean("hostOnline", hostOnline);
        writer.WriteString("appVersion", CustomWidgetAgentConnectorService.GetAppVersionString());
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("activeUiCulture", resolvedCulture);
        writer.WriteString("widgetsRoot", CustomWidgetDataStore.GetWidgetsRootDirectory());
        int rawW = defaultWidgetWidth is > 0 ? defaultWidgetWidth.Value : (int)SettingsService.DefaultWidgetWidth;
        int rawH = defaultWidgetHeight is > 0 ? defaultWidgetHeight.Value : (int)SettingsService.DefaultWidgetHeight;
        int hostDefaultW = Math.Clamp(rawW, 180, 1200);
        int hostDefaultH = Math.Clamp(rawH, 80, 1200);
        writer.WriteStartObject("hostDefaultSize");
        writer.WriteNumber("width", hostDefaultW);
        writer.WriteNumber("height", hostDefaultH);
        writer.WriteEndObject();
        writer.WriteStartArray("pendingStages");
        foreach (AgentStageSummary stage in CustomWidgetAgentStageStore.ListPending())
        {
            writer.WriteStartObject();
            writer.WriteString("stageId", stage.StageId);
            if (!string.IsNullOrWhiteSpace(stage.WidgetId))
            {
                writer.WriteString("widgetId", stage.WidgetId);
            }
            writer.WriteString("createdAtUtc", stage.CreatedUtc.ToString("O"));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    internal static string BuildValidationReportJson(
        string rawManifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales,
        IReadOnlyList<CustomWidgetValidationDiagnostic>? extraDiagnostics,
        bool indented,
        out bool isValid)
    {
        string syncedManifestJson = !string.IsNullOrWhiteSpace(logicJs)
            ? CustomWidgetTemplates.SyncManifestScriptPropertyJson(rawManifestJson, logicJs)
            : rawManifestJson;
        CustomWidgetCompilerResult compiled = CustomWidgetCompilerPipeline.Compile(
            syncedManifestJson,
            logicJs,
            locales,
            sampleDataJson: null,
            cultureName: null,
            storePayload: null,
            runOnInitDryRun: true,
            normalizeDom: true,
            autoWireCapabilities: true);

        int extraErrorCount = extraDiagnostics?.Count(static d => d.IsError) ?? 0;
        isValid = compiled.Manifest is not null && !compiled.HasHardErrors && extraErrorCount == 0;
        bool validResult = isValid;

        var buffer = new ArrayBufferWriter<byte>(2048);
        using (var w = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteBoolean("isValid", validResult);
            if (compiled.Manifest is not null)
            {
                w.WriteString("id", compiled.Manifest.Id);
                w.WriteString("name", compiled.Manifest.Name);
            }
            if (!string.IsNullOrWhiteSpace(compiled.NormalizedManifestJson))
            {
                w.WriteString("normalizedManifestJson", compiled.NormalizedManifestJson);
            }
            w.WriteStartArray("diagnostics");
            if (extraDiagnostics is { Count: > 0 })
            {
                foreach (CustomWidgetValidationDiagnostic d in extraDiagnostics.Where(static x => x.IsError))
                {
                    WriteValidationDiagnosticObject(w, d.Path, "error", d.Message, d.Code);
                }
            }
            foreach (CustomWidgetValidationDiagnostic d in compiled.HardDiagnostics)
            {
                WriteValidationDiagnosticObject(w, d.Path, "error", d.Message, d.Code);
            }
            if (extraDiagnostics is { Count: > 0 })
            {
                foreach (CustomWidgetValidationDiagnostic d in extraDiagnostics.Where(static x => !x.IsError))
                {
                    WriteValidationDiagnosticObject(w, d.Path, "warning", d.Message, d.Code);
                }
            }
            foreach (CustomWidgetCompilerDiagnostic s in compiled.SoftDiagnostics)
            {
                WriteValidationDiagnosticObject(w, s.Path, "warning", s.Message, s.Code);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteValidationDiagnosticObject(
        Utf8JsonWriter w,
        string path,
        string severity,
        string message,
        string? code)
    {
        w.WriteStartObject();
        w.WriteString("path", path);
        w.WriteString("severity", severity);
        w.WriteString("message", message);
        if (!string.IsNullOrWhiteSpace(code))
        {
            w.WriteString("code", code);
        }
        w.WriteEndObject();
    }

    internal static void WriteWidgetListItemJson(
        Utf8JsonWriter writer,
        string widgetId,
        string name,
        bool? enabled,
        CustomWidgetManifest? manifest,
        string directory,
        string manifestPath,
        string? activeCulture = null)
    {
        writer.WriteStartObject();
        writer.WriteString("widgetId", widgetId);
        writer.WriteString("name", name);
        if (enabled is { } en)
        {
            writer.WriteBoolean("enabled", en);
        }
        writer.WriteString("manifestId", manifest?.Id ?? string.Empty);
        writer.WriteString("origin", manifest?.Origin ?? CustomWidgetOriginKinds.Local);
        writer.WriteBoolean("isCustomized", manifest?.IsCustomized == true);
        if (!string.IsNullOrWhiteSpace(manifest?.ForkedFromId))
        {
            writer.WriteString("forkedFromId", manifest!.ForkedFromId);
        }
        if (!string.IsNullOrWhiteSpace(manifest?.ForkedFromVersion))
        {
            writer.WriteString("forkedFromVersion", manifest!.ForkedFromVersion);
        }
        writer.WriteString("icon", manifest?.Icon ?? string.Empty);
        if (activeCulture is not null)
        {
            writer.WriteString("description", manifest?.ResolveLocalizedDescription(activeCulture) ?? string.Empty);
        }
        writer.WriteString("directory", directory);
        writer.WriteString("manifestPath", manifestPath);
        writer.WriteEndObject();
    }

    internal static void WriteWidgetInspectionBodyJson(
        Utf8JsonWriter writer,
        string widgetId,
        string name,
        CustomWidgetManifest? parsedManifest,
        bool? visible,
        string directory,
        string manifestPath,
        string manifestJson,
        string? logicPath,
        string? logicJs,
        string localesDir,
        CustomWidgetLocaleCatalog? locales,
        CustomWidgetLiveRuntimeSnapshot? liveSnapshot,
        CustomWidgetStorePayload? diskPayload,
        bool isOfflineScan)
    {
        if (isOfflineScan)
        {
            writer.WriteString("status", "offline-disk-scan");
        }
        writer.WriteString("widgetId", widgetId);
        writer.WriteString("name", name);
        if (isOfflineScan)
        {
            writer.WriteString("manifestId", parsedManifest?.Id ?? string.Empty);
        }
        writer.WriteString("origin", parsedManifest?.Origin ?? CustomWidgetOriginKinds.Local);
        writer.WriteBoolean("isCustomized", parsedManifest?.IsCustomized == true);
        if (!string.IsNullOrWhiteSpace(parsedManifest?.ForkedFromId))
        {
            writer.WriteString("forkedFromId", parsedManifest!.ForkedFromId);
        }
        if (!string.IsNullOrWhiteSpace(parsedManifest?.ForkedFromVersion))
        {
            writer.WriteString("forkedFromVersion", parsedManifest!.ForkedFromVersion);
        }
        if (visible is { } vis)
        {
            writer.WriteBoolean("visible", vis);
        }
        writer.WriteBoolean("liveLoaded", liveSnapshot?.LiveLoaded == true);
        writer.WriteString("displayMode", liveSnapshot?.DisplayMode ?? "offline");
        writer.WriteString("directory", directory);
        writer.WriteString("manifestPath", manifestPath);
        writer.WriteString("manifestJson", manifestJson);
        bool hasLogic = !string.IsNullOrWhiteSpace(logicJs);
        writer.WriteBoolean("hasLogicJs", hasLogic);
        if (hasLogic)
        {
            if (!string.IsNullOrWhiteSpace(logicPath))
            {
                writer.WriteString("logicPath", logicPath);
            }
            writer.WriteString("logicJs", logicJs);
        }
        bool hasLocales = locales is { HasAnyLocale: true };
        writer.WriteBoolean("hasLocales", hasLocales);
        if (hasLocales)
        {
            writer.WriteString("localesDir", localesDir);
            writer.WritePropertyName("locales");
            locales!.WriteCatalogObjectTo(writer);
        }

        if (isOfflineScan && liveSnapshot is null && diskPayload is null)
        {
            return;
        }

        writer.WriteStartObject("state");
        if (liveSnapshot is not null)
        {
            foreach ((string k, JsonElement el) in liveSnapshot.StateJson)
            {
                if (el.ValueKind != JsonValueKind.Undefined)
                {
                    writer.WritePropertyName(k);
                    el.WriteTo(writer);
                }
            }
            foreach ((string k, string v) in liveSnapshot.State)
            {
                if (!liveSnapshot.StateJson.ContainsKey(k))
                {
                    writer.WriteString(k, v);
                }
            }
        }
        else if (diskPayload is not null)
        {
            foreach ((string k, JsonElement el) in diskPayload.StateJson)
            {
                if (el.ValueKind != JsonValueKind.Undefined)
                {
                    writer.WritePropertyName(k);
                    el.WriteTo(writer);
                }
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
            foreach ((string col, List<Dictionary<string, JsonElement>> sampleList) in liveSnapshot.SampleItemsJson)
            {
                writer.WriteStartArray(col);
                foreach (Dictionary<string, JsonElement> sample in sampleList)
                {
                    writer.WriteStartObject();
                    foreach ((string fk, JsonElement fv) in sample)
                    {
                        if (fv.ValueKind != JsonValueKind.Undefined)
                        {
                            writer.WritePropertyName(fk);
                            fv.WriteTo(writer);
                        }
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
                    foreach ((string fk, JsonElement fv) in list[i].FieldsJson)
                    {
                        if (!string.Equals(fk, "id", StringComparison.Ordinal) &&
                            fv.ValueKind != JsonValueKind.Undefined)
                        {
                            writer.WritePropertyName(fk);
                            fv.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
        }
        writer.WriteEndObject();
    }

    private async Task<string> HandleSyncIconAsync(JsonElement root)
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
            return BuildErrorJson("widgetId is required.");
        }

        string? extractedIcon = null;
        bool hasIconValue = false;
        if (root.TryGetProperty("icon", out JsonElement iconEl))
        {
            if (iconEl.ValueKind == JsonValueKind.Null)
            {
                extractedIcon = null;
                hasIconValue = true;
            }
            else if (iconEl.ValueKind == JsonValueKind.String)
            {
                extractedIcon = iconEl.GetString();
                hasIconValue = true;
            }
            else
            {
                return BuildErrorJson("icon must be a string or null.");
            }
        }
        else if (root.TryGetProperty("manifestJson", out JsonElement mjEl) && mjEl.ValueKind == JsonValueKind.String)
        {
            string manifestJson = mjEl.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(manifestJson))
            {
                return BuildErrorJson("icon or manifestJson is required.");
            }

            CustomWidgetManifest? parsedManifest = CustomWidgetManifestParser.Parse(manifestJson).Manifest;
            if (parsedManifest is null)
            {
                return BuildErrorJson("Invalid manifestJson.");
            }

            extractedIcon = CustomWidgetTemplates.IsEmojiIcon(parsedManifest.Icon)
                ? parsedManifest.Icon.Trim()
                : null;
            hasIconValue = true;
        }

        if (!hasIconValue)
        {
            return BuildErrorJson("icon or manifestJson is required.");
        }

        try
        {
            GlanceWidgetInstanceInfo? updated = await manager.UpdateCustomWidgetIconOnlyAsync(widgetId, extractedIcon);
            if (updated is null)
            {
                return BuildErrorJson($"Custom widget '{widgetId}' not found.");
            }

            return BuildSuccessJson(w => w.WriteString("widgetId", updated.Id));
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return BuildErrorJson(ex.Message);
        }
    }

    private async Task<string> HandleListAsync()
    {
        WidgetManager? manager = _widgetManagerProvider();
        if (manager is null)
        {
            return BuildErrorJson("DeskBox WidgetManager is still initializing.");
        }

        IReadOnlyList<GlanceWidgetInstanceInfo> instances = manager.GetCustomWidgetInstances();
        var items = new List<(GlanceWidgetInstanceInfo Info, CustomWidgetManifest? Manifest, string Dir, string ManifestPath)>(instances.Count);

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
                    if (parsedManifest is not null && await store.ReadLocaleCatalogAsync() is { HasAnyLocale: true } locales)
                    {
                        parsedManifest = parsedManifest.WithLocales(locales);
                    }
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
            writer.WriteString("activeLocale", manager.ActiveCultureName);
            writer.WriteStartArray("widgets");
            foreach (var item in items)
            {
                WriteWidgetListItemJson(
                    writer,
                    item.Info.Id,
                    item.Info.Name,
                    item.Info.IsEnabled,
                    item.Manifest,
                    item.Dir,
                    item.ManifestPath,
                    manager.ActiveCultureName);
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

        WidgetConfig? config = manager.ResolveCustomWidgetConfig(widgetId, allowTemplateMetadataFallback: true);
        if (config is null)
        {
            return BuildErrorJson($"Custom widget '{widgetId}' not found.");
        }

        CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(config.Id);
        string manifestJson = await store.ReadOrCreateManifestJsonAsync();
        CustomWidgetManifest? parsedManifest = CustomWidgetManifestParser.Parse(manifestJson).Manifest;
        string? logicJs = await store.ReadLogicJsOrNullAsync();
        CustomWidgetLocaleCatalog? locales = await store.ReadLocaleCatalogAsync();
        CustomWidgetLiveRuntimeSnapshot? liveSnapshot = await manager.TryGetCustomWidgetLiveSnapshotAsync(config.Id);
        CustomWidgetStorePayload? diskPayload = liveSnapshot is null ? await store.LoadAsync() : null;

        return BuildSuccessJson(writer =>
        {
            WriteWidgetInspectionBodyJson(
                writer,
                config.Id,
                config.Name,
                parsedManifest,
                config.IsVisible && !config.IsDisabled,
                store.WidgetDirectory,
                store.ManifestFilePath,
                manifestJson,
                store.LogicJsPath,
                logicJs,
                store.LocalesDirectoryPath,
                locales,
                liveSnapshot,
                diskPayload,
                isOfflineScan: false);
        });
    }

    private static string? ReadStageId(JsonElement root) =>
        root.TryGetProperty("stageId", out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private string HandleStageList()
    {
        IReadOnlyList<AgentStageSummary> pending = CustomWidgetAgentStageStore.ListPending();
        return BuildSuccessJson(writer =>
        {
            writer.WriteNumber("count", pending.Count);
            writer.WriteStartArray("stages");
            foreach (AgentStageSummary item in pending)
            {
                writer.WriteStartObject();
                writer.WriteString("stageId", item.StageId);
                if (!string.IsNullOrWhiteSpace(item.WidgetId))
                {
                    writer.WriteString("widgetId", item.WidgetId);
                }
                writer.WriteString("createdUtc", item.CreatedUtc.ToString("O"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
    }

    private string HandleStageGet(JsonElement root)
    {
        string? culture = _widgetManagerProvider()?.ActiveCultureName;
        if (!CustomWidgetAgentStageStore.TryReadBundle(ReadStageId(root), out AgentStageBundle? bundle, out string error, culture) ||
            bundle is null)
        {
            return BuildErrorJson(error);
        }

        return BuildSuccessJson(writer =>
        {
            writer.WriteString("stageId", bundle.StageId);
            if (!string.IsNullOrWhiteSpace(bundle.WidgetId))
            {
                writer.WriteString("widgetId", bundle.WidgetId);
            }
            writer.WriteString("manifestJson", bundle.ManifestJson);
            writer.WriteBoolean("hasLogicJs", bundle.HasLogicJs);
            writer.WriteBoolean("hasLocales", bundle.HasLocales);
            writer.WriteString("createdUtc", bundle.CreatedUtc.ToString("O"));
        });
    }

    /// <summary>
    /// 用户确认入口：从暂存区读回完整内容，构造一次带 commit=true 的 upsert，
    /// 复用同一条校验 / stageId 消费 / 落盘路径，避免两套提交语义。
    /// </summary>
    private async Task<string> HandleStageCommitAsync(JsonElement root, bool trustedHostUiInitiated = false)
    {
        string? stageId = ReadStageId(root);
        if (string.IsNullOrWhiteSpace(stageId))
        {
            string? culture = _widgetManagerProvider()?.ActiveCultureName;
            return BuildErrorJson(LocalizationService.TranslateForCulture(
                culture ?? LocalizationService.ResolveAmbientCultureName(),
                "CustomWidget.AgentStage.Error.InvalidId"));
        }

        bool callerConfirmDataLoss = trustedHostUiInitiated ||
            (root.TryGetProperty("confirmDataLoss", out JsonElement cdlEl) && cdlEl.ValueKind == JsonValueKind.True);

        using var buffer = new MemoryStream(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("op", "upsert");
            writer.WriteBoolean("commit", true);
            writer.WriteBoolean("confirmDataLoss", callerConfirmDataLoss);
            writer.WriteString("stageId", stageId);
            writer.WriteEndObject();
        }

        using JsonDocument doc = JsonDocument.Parse(buffer.ToArray());
        return await HandleUpsertAsync(doc.RootElement, trustedHostUiInitiated);
    }

    /// <summary>用户拒绝一份外部暂存：只丢弃批次，绝不写桌面。</summary>
    private string HandleStageDiscard(JsonElement root)
    {
        string? culture = _widgetManagerProvider()?.ActiveCultureName;
        if (!CustomWidgetAgentStageStore.TryDiscard(ReadStageId(root), out string error, culture))
        {
            return BuildErrorJson(error);
        }

        return BuildSuccessJson(writer =>
        {
            writer.WriteBoolean("discarded", true);
            writer.WriteBoolean("committed", false);
        });
    }

    private async Task<string> HandleUpsertAsync(JsonElement root, bool trustedHostUiInitiated = false)
    {
        WidgetManager? manager = _widgetManagerProvider();
        if (manager is null)
        {
            return BuildErrorJson("DeskBox WidgetManager is still initializing.");
        }

        string activeCulture = manager.ActiveCultureName;
        string? widgetId = root.TryGetProperty("widgetId", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;

        bool hasCallerManifest = root.TryGetProperty("manifestJson", out JsonElement mEl) &&
                                 mEl.ValueKind == JsonValueKind.String &&
                                 !string.IsNullOrWhiteSpace(mEl.GetString());
        string? callerManifestJson = hasCallerManifest ? mEl.GetString() : null;
        string manifestJson = callerManifestJson ?? string.Empty;
        bool hasCallerResize = root.TryGetProperty("resizeToDefault", out JsonElement rEl) &&
                               rEl.ValueKind is JsonValueKind.True or JsonValueKind.False;
        bool resizeToDefault = hasCallerResize && rEl.ValueKind == JsonValueKind.True;
        // P2：只有显式 reveal=true 才把窗口带到前台，默认不再自动改变桌面呈现。
        bool reveal = root.TryGetProperty("reveal", out JsonElement revEl) && revEl.ValueKind == JsonValueKind.True;
        // P2：外部写入默认只进入暂存区；commit=true 且携带有效 stageId 才落盘。
        bool commit = root.TryGetProperty("commit", out JsonElement commitEl) && commitEl.ValueKind == JsonValueKind.True;
        bool confirmDataLoss = root.TryGetProperty("confirmDataLoss", out JsonElement confirmLossEl) && confirmLossEl.ValueKind == JsonValueKind.True;
        string? stageId = root.TryGetProperty("stageId", out JsonElement stageEl) && stageEl.ValueKind == JsonValueKind.String
            ? stageEl.GetString()
            : null;

        if (!commit && string.IsNullOrWhiteSpace(widgetId) &&
            manager.ResolveStudioAgentUpsertTargetWidgetId?.Invoke() is { Length: > 0 } studioWaitingTargetId &&
            manager.ResolveCustomWidgetConfig(studioWaitingTargetId, allowTemplateMetadataFallback: false) is { } waitingCfg)
        {
            widgetId = waitingCfg.Id;
        }

        bool hasCallerLogicJs = root.TryGetProperty("logicJs", out JsonElement ljEl) &&
                                ljEl.ValueKind is JsonValueKind.String or JsonValueKind.Null;
        string? callerLogicJs = hasCallerLogicJs && ljEl.ValueKind == JsonValueKind.String
            ? ljEl.GetString()
            : null;
        if (hasCallerLogicJs && string.IsNullOrWhiteSpace(callerLogicJs))
        {
            callerLogicJs = null;
        }

        bool hasCallerLocales = root.TryGetProperty("locales", out JsonElement locEl) &&
                                locEl.ValueKind is JsonValueKind.Object or JsonValueKind.String or JsonValueKind.Null;
        // 暂存需要保存原始 locales 文本，提交时才能无损还原。
        string? callerLocalesRaw = hasCallerLocales && locEl.ValueKind is not JsonValueKind.Null
            ? locEl.GetRawText()
            : null;

        bool updateLogicJs = hasCallerLogicJs;
        string? logicJs = callerLogicJs;
        bool updateLocales = hasCallerLocales;
        string? localesRaw = callerLocalesRaw;
        CustomWidgetLocaleCatalog? locales = null;
        if (hasCallerLocales)
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

        // commit=true 时支持仅传 stageId（无需重复传整份 manifestJson），提前从暂存包回填 manifestJson / logicJs / locales / resizeToDefault，
        // 既避免校验阶段丢失已暂存的脚本/语言包，又避免落盘时把已暂存的 logic.js 与 locales 覆盖清空。
        bool hasInlineManifestEdits =
            root.TryGetProperty("upsertElements", out _) ||
            root.TryGetProperty("removeElements", out _) ||
            root.TryGetProperty("patchRoot", out _) ||
            root.TryGetProperty("removeRootKeys", out _);

        AgentStageBundle? stagedPreview = null;
        if (commit && !string.IsNullOrWhiteSpace(stageId))
        {
            if (CustomWidgetAgentStageStore.TryReadBundle(stageId, out stagedPreview, out string readErr, activeCulture) &&
                stagedPreview is not null)
            {
                if (!hasCallerManifest ||
                    (!hasInlineManifestEdits && CustomWidgetAgentStageStore.PreviewMatchesCallerManifest(stageId, callerManifestJson)))
                {
                    manifestJson = stagedPreview.ManifestJson;
                }

                if (!string.IsNullOrWhiteSpace(widgetId) &&
                    !string.Equals(widgetId, stagedPreview.WidgetId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    return BuildErrorJson("widgetId does not match the target bound to this stageId.");
                }

                widgetId = stagedPreview.WidgetId;

                if (!hasCallerLogicJs && stagedPreview.HasLogicJs)
                {
                    updateLogicJs = true;
                    logicJs = string.IsNullOrWhiteSpace(stagedPreview.LogicJs) ? null : stagedPreview.LogicJs;
                }

                if (!hasCallerLocales && stagedPreview.HasLocales)
                {
                    updateLocales = true;
                    localesRaw = stagedPreview.LocalesJson;
                    locales = string.IsNullOrWhiteSpace(stagedPreview.LocalesJson)
                        ? null
                        : CustomWidgetLocaleCatalog.ParseCatalogJson(stagedPreview.LocalesJson);
                }

                if (!hasCallerResize && stagedPreview.ResizeToDefault)
                {
                    resizeToDefault = true;
                }
            }
            else if (!hasCallerManifest)
            {
                if (CustomWidgetAgentStageStore.TryGetCommitted(
                        stageId,
                        out string? prevIdempotentId,
                        expectedManifestJson: null,
                        hasCallerLogicJs: hasCallerLogicJs,
                        callerLogicJs: callerLogicJs,
                        hasCallerLocales: hasCallerLocales,
                        callerLocalesJson: callerLocalesRaw))
                {
                    string resolvedIdempotentId = !string.IsNullOrWhiteSpace(prevIdempotentId)
                        ? prevIdempotentId!
                        : (widgetId ?? string.Empty);
                    return BuildSuccessJson(writer =>
                    {
                        writer.WriteString("widgetId", resolvedIdempotentId);
                        writer.WriteBoolean("committed", true);
                        writer.WriteBoolean("staged", false);
                        writer.WriteBoolean("idempotent", true);
                        writer.WriteString("note", "This stageId was already committed; duplicate commit treated as idempotent no-op.");
                    });
                }

                return BuildErrorJson(readErr);
            }
            else if (string.IsNullOrWhiteSpace(widgetId) &&
                     CustomWidgetAgentStageStore.TryGetCommitted(
                         stageId,
                         out string? prevCommittedId,
                         callerManifestJson,
                         hasCallerLogicJs,
                         callerLogicJs,
                         hasCallerLocales,
                         callerLocalesRaw) &&
                     !string.IsNullOrWhiteSpace(prevCommittedId))
            {
                widgetId = prevCommittedId;
            }
        }
        else if (commit && string.IsNullOrWhiteSpace(stageId))
        {
            return BuildErrorJson(LocalizationService.TranslateForCulture(
                activeCulture,
                "CustomWidget.AgentStage.Error.MissingStageIdForCommit"));
        }

        bool hasScriptHunks = root.TryGetProperty("scriptHunks", out JsonElement shEl) &&
                              shEl.ValueKind == JsonValueKind.Array &&
                              shEl.GetArrayLength() > 0;
        if (commit && (hasInlineManifestEdits || hasScriptHunks))
        {
            return BuildErrorJson(
                "Inline manifest edits (upsertElements/removeElements/patchRoot/removeRootKeys) and scriptHunks cannot be applied during commit=true; stage a fresh preview first.");
        }

        StudioFlatManifestEditRequest? flatManifestEdit = null;
        if (hasInlineManifestEdits)
        {
            if (!StudioToolTransport.TryParseEditManifestArgs(root.GetRawText(), out StudioFlatManifestEditRequest? parsedFlatEdit, out string? flatErr))
            {
                return BuildErrorJson(flatErr ?? "Invalid flat manifest edit arguments.");
            }
            flatManifestEdit = parsedFlatEdit;
        }

        List<StudioScriptPatchHunk>? scriptHunks = null;
        if (hasScriptHunks)
        {
            if (!StudioToolTransport.TryParsePatchScriptArgs(shEl.GetRawText(), out List<StudioScriptPatchHunk> parsedHunks, out string? hunksErr))
            {
                return BuildErrorJson(hunksErr ?? "Invalid scriptHunks.");
            }
            scriptHunks = parsedHunks;
        }

        WidgetConfig? existingTarget = !string.IsNullOrWhiteSpace(widgetId)
            ? manager.ResolveCustomWidgetConfig(widgetId, allowTemplateMetadataFallback: false)
            : null;
        if (existingTarget is not null)
        {
            widgetId = existingTarget.Id;
        }
        CustomWidgetDataStore? existingStore = existingTarget is not null
            ? CustomWidgetDataStore.ForWidget(existingTarget.Id)
            : null;
        string? existingDiskManifest = existingStore is not null
            ? await existingStore.TryReadExistingManifestJsonAsync()
            : null;
        string? existingDiskLogicJs = existingStore is not null
            ? await existingStore.ReadLogicJsOrNullAsync()
            : null;
        CustomWidgetStorePayload? existingLiveStore = existingStore is not null
            ? await existingStore.LoadAsync()
            : null;

        if (flatManifestEdit is not null || scriptHunks is { Count: > 0 })
        {
            if (string.IsNullOrWhiteSpace(manifestJson))
            {
                if (existingTarget is null)
                {
                    return BuildErrorJson("Existing widgetId is required when patching with upsertElements/removeElements/patchRoot/removeRootKeys or scriptHunks without manifestJson.");
                }

                if (string.IsNullOrWhiteSpace(existingDiskManifest))
                {
                    return BuildErrorJson($"Could not load manifest.json for widget '{existingTarget.Id}'.");
                }
                manifestJson = existingDiskManifest;
            }

            string? baseJsForPatch = updateLogicJs ? logicJs : existingDiskLogicJs;
            var patchEngine = new StudioWorkspaceEngine(manifestJson, baseJsForPatch);
            if (flatManifestEdit is not null)
            {
                StudioWorkspaceOpResult flatRes = patchEngine.ApplyFlatManifestEdit(flatManifestEdit);
                if (!flatRes.Succeeded)
                {
                    return BuildErrorJson(flatRes.ErrorMessage ?? flatRes.Summary);
                }
                manifestJson = patchEngine.GetManifestJson(indented: true) ?? manifestJson;
            }

            if (scriptHunks is { Count: > 0 })
            {
                StudioWorkspaceOpResult jsRes = patchEngine.ApplyScriptPatch(scriptHunks);
                if (!jsRes.Succeeded)
                {
                    return BuildErrorJson(jsRes.ErrorMessage ?? jsRes.Summary);
                }
                updateLogicJs = true;
                logicJs = patchEngine.LogicJs;
            }
        }

        string effectiveManifestJson = updateLogicJs
            ? CustomWidgetTemplates.SyncManifestScriptPropertyJson(manifestJson, logicJs)
            : manifestJson;

        string? validationLogicJs = updateLogicJs
            ? logicJs
            : existingDiskLogicJs;
        CustomWidgetLocaleCatalog? validationLocales = updateLocales
            ? locales
            : (existingStore is not null
                ? await existingStore.ReadLocaleCatalogAsync()
                : null);

        CustomWidgetCompilerResult compiled = CustomWidgetCompilerPipeline.Compile(
            effectiveManifestJson,
            validationLogicJs,
            validationLocales,
            sampleDataJson: null,
            cultureName: activeCulture,
            storePayload: existingLiveStore,
            runOnInitDryRun: true,
            normalizeDom: true,
            autoWireCapabilities: true);
        if (compiled.Manifest is null || compiled.HasHardErrors)
        {
            string firstErr = compiled.HardDiagnostics.FirstOrDefault()?.Message
                ?? compiled.ValidationResult.Diagnostics.FirstOrDefault(static d => d.IsError)?.Message
                ?? "Invalid manifest.json";
            return BuildErrorJson(firstErr);
        }

        CustomWidgetManifest? previousManifest = !string.IsNullOrWhiteSpace(existingDiskManifest)
            ? CustomWidgetManifestParser.Parse(existingDiskManifest).Manifest
            : null;
        CustomWidgetDataSafetyReport? dataSafetyReport = existingTarget is not null && existingLiveStore is not null
            ? CustomWidgetHealthAnalyzer.EvaluateStoreDataSafety(
                previousManifest,
                existingLiveStore,
                compiled.Manifest)
            : null;

        // P2 默认路径：校验通过后只登记暂存批次，绝不写工作区、绝不改桌面配置。
        if (!commit)
        {
            string manifestToStage = !string.IsNullOrWhiteSpace(compiled.NormalizedManifestJson)
                ? compiled.NormalizedManifestJson
                : effectiveManifestJson;
            bool preserveCommunityStageOrigin = string.Equals(
                compiled.Manifest.Origin,
                CustomWidgetOriginKinds.Community,
                StringComparison.OrdinalIgnoreCase);
            if (!preserveCommunityStageOrigin && existingTarget is not null)
            {
                preserveCommunityStageOrigin = !CustomWidgetTrustStore.Shared
                    .GetProfile(existingTarget.Id, previousManifest?.Origin)
                    .TrustedLocalAuthoring;
            }

            manifestToStage = CustomWidgetManifestMetadataStamper.StampProvenance(
                manifestToStage,
                origin: preserveCommunityStageOrigin ? CustomWidgetOriginKinds.Community : CustomWidgetOriginKinds.Agent,
                isCustomized: true);

            string newStageId = CustomWidgetAgentStageStore.Stage(new AgentStageRequest(
                widgetId,
                manifestToStage,
                logicJs,
                updateLogicJs,
                localesRaw,
                updateLocales,
                resizeToDefault,
                RawCallerManifestJson: callerManifestJson));
            return BuildSuccessJson(writer =>
            {
                writer.WriteBoolean("staged", true);
                writer.WriteBoolean("committed", false);
                writer.WriteString("stageId", newStageId);
                if (!string.IsNullOrWhiteSpace(widgetId))
                {
                    writer.WriteString("widgetId", widgetId);
                }
                if (dataSafetyReport?.HasDataLossRisk == true)
                {
                    WriteDataSafetyRiskProperty(writer, dataSafetyReport);
                    writer.WriteString("next",
                        "Validated and staged in DeskBox Workshop (desktop unchanged), but committing would drop existing user data (see dataSafetyRisk). Confirm with the user, then call deskbox_upsert_widget again with commit=true, confirmDataLoss=true, and the same stageId.");
                }
                else
                {
                    writer.WriteString("next",
                        "Validated and staged in DeskBox Workshop (desktop unchanged). Share this stageId with the user; after explicit user confirmation, call deskbox_upsert_widget again with commit=true and the same stageId (manifestJson is optional on commit).");
                }
                WriteCompilerWarningsArray(writer, compiled);
            });
        }

        // Decision 5A: Evaluate data loss BEFORE TryConsumeBundle so a blocked commit retains stageId unconsumed.
        if (stagedPreview is not null && dataSafetyReport?.HasDataLossRisk == true && !confirmDataLoss)
        {
            return BuildSuccessJson(writer =>
            {
                writer.WriteBoolean("committed", false);
                writer.WriteBoolean("staged", true);
                writer.WriteBoolean("requiresDataLossConfirmation", true);
                if (!string.IsNullOrWhiteSpace(stageId))
                {
                    writer.WriteString("stageId", stageId);
                }
                writer.WriteString("widgetId", !string.IsNullOrWhiteSpace(widgetId) ? widgetId : compiled.Manifest.Id);
                WriteDataSafetyRiskProperty(writer, dataSafetyReport);
                writer.WriteString("next",
                    "Commit paused by data safety gate: existing collections or non-empty state keys would be dropped. Confirm with the user and call deskbox_upsert_widget again with commit=true, the same stageId, and confirmDataLoss=true.");
                WriteCompilerWarningsArray(writer, compiled);
            });
        }

        // P2 提交路径：先抢占三态提交租约（s_pendingCommits），避免审批弹窗等待期间客户端重试触发重复弹窗或并发提交。
        if (!CustomWidgetAgentStageStore.TryConsumeBundle(
                stageId,
                callerManifestJson,
                hasCallerLogicJs,
                callerLogicJs,
                hasCallerLocales,
                callerLocalesRaw,
                out AgentStageBundle? consumedBundle,
                out string stageError,
                activeCulture))
        {
            // 幂等：同一批次被重复提交（客户端重试、Agent 重放）时不应报「stageId 不存在」。
            if (CustomWidgetAgentStageStore.TryGetCommitted(
                    stageId,
                    out string? committedWidgetId,
                    callerManifestJson,
                    hasCallerLogicJs,
                    callerLogicJs,
                    hasCallerLocales,
                    callerLocalesRaw))
            {
                string idempotentWidgetId = !string.IsNullOrWhiteSpace(committedWidgetId)
                    ? committedWidgetId!
                    : compiled.Manifest.Id;
                return BuildSuccessJson(writer =>
                {
                    writer.WriteString("widgetId", idempotentWidgetId);
                    writer.WriteBoolean("committed", true);
                    writer.WriteBoolean("staged", false);
                    writer.WriteBoolean("idempotent", true);
                    writer.WriteString("note", "This stageId was already committed; duplicate commit treated as idempotent no-op.");
                });
            }

            return BuildErrorJson(stageError);
        }

        // Host-side approval gate: an external Agent commit that overwrites an existing
        // widget, drops its data, or uses scripts / host-access capabilities must be
        // approved in DeskBox's own UI. Commits initiated directly by the user inside
        // DeskBox Workshop (trustedHostUiInitiated) already reflect explicit user action.
        if (!trustedHostUiInitiated &&
            commit &&
            !string.IsNullOrWhiteSpace(stageId) &&
            _commitApprovalProvider is not null)
        {
            string approvalWidgetId = existingTarget?.Id
                ?? (!string.IsNullOrWhiteSpace(widgetId) ? widgetId! : compiled.Manifest.Id);
            string approvalWidgetName = compiled.Manifest.ResolveLocalizedName(activeCulture);
            if (string.IsNullOrWhiteSpace(approvalWidgetName))
            {
                approvalWidgetName = existingTarget?.Name ?? approvalWidgetId;
            }

            bool overwritesExisting = existingTarget is not null;
            bool clearsExistingData = dataSafetyReport?.HasDataLossRisk == true;
            bool declaresPrivileged = CustomWidgetTrustStore.HasPrivilegedSurfaceInManifestOrScript(
                compiled.Manifest,
                !string.IsNullOrWhiteSpace(compiled.NormalizedManifestJson) ? compiled.NormalizedManifestJson : effectiveManifestJson,
                validationLogicJs);
            if (overwritesExisting || clearsExistingData || declaresPrivileged)
            {
                bool approved;
                try
                {
                    approved = await _commitApprovalProvider(new AgentCommitApprovalRequest(
                        approvalWidgetId,
                        overwritesExisting,
                        clearsExistingData,
                        declaresPrivileged,
                        approvalWidgetName)).ConfigureAwait(false);
                }
                catch
                {
                    CustomWidgetAgentStageStore.AbortCommit(stageId!);
                    throw;
                }

                if (!approved)
                {
                    CustomWidgetAgentStageStore.AbortCommit(stageId!);
                    return BuildSuccessJson(writer =>
                    {
                        writer.WriteBoolean("committed", false);
                        writer.WriteBoolean("staged", true);
                        writer.WriteBoolean("requiresHostApproval", true);
                        writer.WriteString("stageId", stageId);
                        writer.WriteString("widgetId", approvalWidgetId);
                        writer.WriteString("next",
                            "The user declined this commit in DeskBox (or the approval prompt timed out). The staged bundle is preserved; do not retry without a fresh explicit user request.");
                    });
                }

                if (!CustomWidgetAgentStageStore.HasPendingCommitLease(stageId!))
                {
                    return BuildErrorJson("Staged commit was cancelled while awaiting approval.");
                }
            }
        }

        if (consumedBundle is not null)
        {
            if (!hasInlineManifestEdits)
            {
                manifestJson = consumedBundle.ManifestJson;
            }

            if (string.IsNullOrWhiteSpace(widgetId))
            {
                widgetId = consumedBundle.WidgetId;
            }
            if (!hasCallerLogicJs && consumedBundle.HasLogicJs)
            {
                updateLogicJs = true;
                logicJs = string.IsNullOrWhiteSpace(consumedBundle.LogicJs) ? null : consumedBundle.LogicJs;
            }

            if (!hasCallerLocales && consumedBundle.HasLocales)
            {
                updateLocales = true;
                locales = string.IsNullOrWhiteSpace(consumedBundle.LocalesJson)
                    ? null
                    : CustomWidgetLocaleCatalog.ParseCatalogJson(consumedBundle.LocalesJson);
            }

            if (!hasCallerResize && consumedBundle.ResizeToDefault)
            {
                resizeToDefault = true;
            }
        }

        string manifestToCommit = !string.IsNullOrWhiteSpace(compiled.NormalizedManifestJson)
            ? compiled.NormalizedManifestJson
            : manifestJson;

        GlanceWidgetInstanceInfo info;
        try
        {
            info = await manager.UpsertCustomWidgetFromAgentAsync(
                widgetId,
                manifestToCommit,
                resizeToDefault,
                reveal,
                logicJs,
                updateLogicJs,
                locales,
                updateLocales);
        }
        catch
        {
            // Keep the staged bundle retryable; only success consumes the lease.
            CustomWidgetAgentStageStore.AbortCommit(stageId!);
            throw;
        }

        CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(info.Id);
        CustomWidgetAgentStageStore.CompleteCommit(stageId!, info.Id);

        return BuildSuccessJson(writer =>
        {
            writer.WriteString("widgetId", info.Id);
            writer.WriteString("name", info.Name);
            writer.WriteBoolean("committed", true);
            writer.WriteBoolean("staged", false);
            writer.WriteBoolean("enabled", info.IsEnabled);
            writer.WriteString("directory", store.WidgetDirectory);
            writer.WriteString("manifestPath", store.ManifestFilePath);
            WriteCompilerWarningsArray(writer, compiled);
        });
    }

    private static void WriteDataSafetyRiskProperty(Utf8JsonWriter writer, CustomWidgetDataSafetyReport report)
    {
        writer.WritePropertyName("dataSafetyRisk");
        writer.WriteStartObject();
        writer.WriteBoolean("hasDataLossRisk", report.HasDataLossRisk);
        writer.WriteStartArray("droppedCollections");
        foreach (CustomWidgetDroppedCollectionRisk col in report.DroppedCollections)
        {
            writer.WriteStartObject();
            writer.WriteString("collection", col.OldCollectionKey);
            writer.WriteNumber("liveRecordCount", col.LiveRecordCount);
            if (!string.IsNullOrWhiteSpace(col.SuggestedNewCollectionKey))
            {
                writer.WriteString("suggestedNewCollectionKey", col.SuggestedNewCollectionKey);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("droppedNonEmptyStateKeys");
        foreach (string key in report.DroppedNonEmptyStateKeys)
        {
            writer.WriteStringValue(key);
        }
        writer.WriteEndArray();
        if (report.PromotedStateKeys is { Count: > 0 })
        {
            writer.WriteStartArray("promotedStateKeys");
            foreach (string key in report.PromotedStateKeys)
            {
                writer.WriteStringValue(key);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static void WriteCompilerWarningsArray(Utf8JsonWriter writer, CustomWidgetCompilerResult compiled)
    {
        writer.WriteStartArray("warnings");
        foreach (CustomWidgetCompilerDiagnostic soft in compiled.SoftDiagnostics)
        {
            writer.WriteStringValue($"{soft.Path}: [{soft.Code}] {soft.Message}");
        }
        writer.WriteEndArray();
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
        Dictionary<string, JsonElement>? stateJsonUpdates = null;
        if (root.TryGetProperty("state", out JsonElement stateEl) && stateEl.ValueKind == JsonValueKind.Object)
        {
            stateUpdates = new Dictionary<string, string>(StringComparer.Ordinal);
            stateJsonUpdates = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty prop in stateEl.EnumerateObject())
            {
                stateJsonUpdates[prop.Name] = prop.Value.Clone();
                stateUpdates[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.GetRawText();
            }
        }

        stateUpdates ??= new Dictionary<string, string>(StringComparer.Ordinal);
        stateJsonUpdates ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!stateUpdates.ContainsKey("updatedAt") && !stateJsonUpdates.ContainsKey("updatedAt"))
        {
            string stamp = DateTimeOffset.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            stateUpdates["updatedAt"] = stamp;
            using JsonDocument stampDoc = JsonDocument.Parse($"\"{stamp}\"");
            stateJsonUpdates["updatedAt"] = stampDoc.RootElement.Clone();
        }

        string? collectionName = root.TryGetProperty("collection", out JsonElement colEl) && colEl.ValueKind == JsonValueKind.String
            ? colEl.GetString()
            : null;
        string? mode = root.TryGetProperty("mode", out JsonElement modeEl) && modeEl.ValueKind == JsonValueKind.String
            ? modeEl.GetString()
            : null;

        List<Dictionary<string, string>>? records = null;
        List<Dictionary<string, JsonElement>>? jsonRecords = null;
        if (root.TryGetProperty("items", out JsonElement itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            records = [];
            jsonRecords = [];
            foreach (JsonElement recEl in itemsEl.EnumerateArray())
            {
                if (recEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                var jsonDict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (JsonProperty fProp in recEl.EnumerateObject())
                {
                    jsonDict[fProp.Name] = fProp.Value.Clone();
                    dict[fProp.Name] = fProp.Value.ValueKind == JsonValueKind.String
                        ? fProp.Value.GetString() ?? string.Empty
                        : fProp.Value.GetRawText();
                }
                records.Add(dict);
                jsonRecords.Add(jsonDict);
            }
        }

        var (ok, resolvedId, err) = await manager.PushCustomWidgetDataAsync(
            widgetId,
            stateUpdates,
            collectionName,
            mode,
            records,
            stateJsonUpdates,
            jsonRecords);

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
        string? stageId = root.TryGetProperty("stageId", out JsonElement sEl) && sEl.ValueKind == JsonValueKind.String
            ? sEl.GetString()
            : null;
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

        if (string.IsNullOrWhiteSpace(stageId) &&
            effectiveMode != "live" &&
            (string.IsNullOrWhiteSpace(widgetId) ||
             !CustomWidgetAgentStageStore.TryFindLatestPendingBundle(widgetId, out _)) &&
            _studioPreviewCapturer is not null &&
            await _studioPreviewCapturer(
                string.IsNullOrWhiteSpace(widgetId) ? null : widgetId,
                effectiveMode == "capsule",
                TimeSpan.FromSeconds(2.5)) is { PngBytes.Length: > 0 } studioCapture)
        {
            return BuildSuccessJson(writer =>
            {
                if (!string.IsNullOrWhiteSpace(widgetId))
                {
                    writer.WriteString("widgetId", widgetId);
                }
                writer.WriteString("mode", effectiveMode);
                writer.WriteNumber("width", studioCapture.Width);
                writer.WriteNumber("height", studioCapture.Height);
                writer.WriteNumber("dpiScale", 1.0);
                writer.WriteNumber("measuredWidth", studioCapture.Width);
                writer.WriteNumber("measuredHeight", studioCapture.Height);
                writer.WriteNumber("verticalOverflowPx", studioCapture.OverflowPx);
                writer.WriteBoolean("hasOverflow", studioCapture.OverflowPx > 0);
                writer.WriteString("mimeType", "image/png");
                writer.WriteBase64String("base64Png", studioCapture.PngBytes);
            });
        }

        CustomWidgetPreviewCaptureResult capture = await manager.CaptureCustomWidgetPreviewWithTelemetryAsync(
            widgetId,
            theme,
            effectiveMode,
            stageId);
        if (capture.PngBytes is null || capture.PngBytes.Length == 0)
        {
            return BuildErrorJson(capture.Error ?? "Failed to capture widget preview.");
        }

        return BuildSuccessJson(writer =>
        {
            if (!string.IsNullOrWhiteSpace(widgetId))
            {
                writer.WriteString("widgetId", widgetId);
            }
            if (!string.IsNullOrWhiteSpace(stageId))
            {
                writer.WriteString("stageId", stageId);
            }
            writer.WriteString("mode", effectiveMode);
            writer.WriteNumber("width", capture.Width);
            writer.WriteNumber("height", capture.Height);
            writer.WriteNumber("dpiScale", Math.Round(capture.DpiScale > 0 ? capture.DpiScale : 1.0, 3));
            writer.WriteNumber("measuredWidth", capture.MeasuredWidth);
            writer.WriteNumber("measuredHeight", capture.MeasuredHeight);
            writer.WriteNumber("verticalOverflowPx", capture.VerticalOverflowPx);
            writer.WriteBoolean("hasOverflow", capture.HasOverflow);
            writer.WriteString("mimeType", "image/png");
            writer.WriteBase64String("base64Png", capture.PngBytes);
        });
    }

    internal static string BuildSuccessJson(Action<Utf8JsonWriter> writeResultObject)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(
                   buffer,
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
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string BuildErrorJson(string message)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", false);
            writer.WriteString("error", message);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ReferenceEquals(s_activeInstance, this))
        {
            s_activeInstance = null;
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

        _connectionSlots.Dispose();
        _cts.Dispose();
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
            _ = DeskBox.Platform.Win32Helper.AllowSetForegroundWindow(uint.MaxValue);

            await using var streamWriter = new StreamWriter(
                client,
                new UTF8Encoding(false),
                bufferSize: 16 * 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            using var reader = new DeskBox.Services.Ipc.PipeBoundedLineReader(client);
            await streamWriter.WriteLineAsync(requestLine.AsMemory(), cancellationToken);
            const int MaxBridgeResponseChars = 32 * 1024 * 1024;
            return await reader.ReadLineAsync(MaxBridgeResponseChars, cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}
