using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.FileSafety;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Per-user, session-scoped Named Pipe bridge allowing external AI Agents (<c>DeskBox.exe --mcp</c>
/// and <c>DeskBox.exe widget ...</c> CLI) to inspect, validate, stage-then-commit, visually capture
/// PNG previews, and push live telemetry to DeskBox Custom Widgets. External writes are staged by
/// default; only an explicit commit (or the Workshop's user confirmation) changes the desktop.
/// </summary>
public sealed class DeskBoxAgentBridgeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<WidgetManager?> _widgetManagerProvider;
    private readonly Func<bool, Task<string>>? _studioContextProvider;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workerTasks = [];
    private readonly object _gate = new();
    private Task? _acceptLoop;
    private volatile bool _disposed;

    public DeskBoxAgentBridgeServer(
        string pipeName,
        Func<WidgetManager?> widgetManagerProvider,
        Func<bool, Task<string>>? studioContextProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(widgetManagerProvider);
        _pipeName = pipeName;
        _widgetManagerProvider = widgetManagerProvider;
        _studioContextProvider = studioContextProvider;
    }

    public string PipeName => _pipeName;

    public void Start()
    {
        if (_acceptLoop is not null || _disposed)
        {
            return;
        }

        _acceptLoop = Task.Run(AcceptLoopAsync);
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
                    writer.WriteString("status", "online");
                    writer.WriteNumber("pid", Environment.ProcessId);
                    writer.WriteString("appVersion", CustomWidgetAgentConnectorService.GetAppVersionString());
                    writer.WriteNumber("schemaVersion", 1);
                    writer.WriteString("widgetsRoot", CustomWidgetDataStore.GetWidgetsRootDirectory());
                    writer.WriteNumber("toolsCount", DeskBoxAgentCliEntry.GetMcpToolSummaries().Count);
                    writer.WriteNumber("skillVersion", CustomWidgetRemotePromptService.Shared.GetEffectivePromptPack().PromptVersion);
                }),

                "spec" => BuildSuccessJson(writer =>
                {
                    writer.WriteString("appVersion", CustomWidgetAgentConnectorService.GetAppVersionString());
                    writer.WriteNumber("schemaVersion", 1);
                    writer.WriteString("widgetsRoot", CustomWidgetDataStore.GetWidgetsRootDirectory());
                    writer.WriteString("skillMarkdown", CustomWidgetTemplates.AiAgentSkillMarkdown);
                    writer.WriteString("manifestJsonSchema", CustomWidgetTemplates.ManifestJsonSchema);
                }),

                "studio_context" => await HandleStudioContextAsync(root),

                "validate" => HandleValidate(root),
                "list" => await HandleListAsync(),
                "get" => await HandleGetAsync(root),
                "upsert" => await HandleUpsertAsync(root),
                "stage_list" => HandleStageList(),
                "stage_get" => HandleStageGet(root),
                "stage_commit" => await HandleStageCommitAsync(root),
                "stage_discard" => HandleStageDiscard(root),
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

    private async Task<string> HandleStudioContextAsync(JsonElement root)
    {
        bool includeManifest = !root.TryGetProperty("includeManifest", out JsonElement incEl) ||
                               incEl.ValueKind != JsonValueKind.False;
        if (_studioContextProvider is not null)
        {
            return await _studioContextProvider(includeManifest);
        }

        return BuildHeadlessStudioContextResponseJson(_widgetManagerProvider()?.ActiveCultureName);
    }

    internal static string BuildHeadlessStudioContextResponseJson(string? activeCulture = null)
    {
        return BuildSuccessJson(writer =>
        {
            WriteCommonStudioContextHeader(writer, hostOnline: true, activeCulture);
            writer.WriteBoolean("isOpen", false);
        });
    }

    internal static void WriteCommonStudioContextHeader(
        Utf8JsonWriter writer,
        bool hostOnline,
        string? activeCulture)
    {
        string resolvedCulture = !string.IsNullOrWhiteSpace(activeCulture)
            ? activeCulture!
            : LocalizationService.ResolvePersistedOrAmbientCultureName();
        writer.WriteBoolean("hostOnline", hostOnline);
        writer.WriteString("appVersion", CustomWidgetAgentConnectorService.GetAppVersionString());
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("activeUiCulture", resolvedCulture);
        writer.WriteString("widgetsRoot", CustomWidgetDataStore.GetWidgetsRootDirectory());
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

    private static string HandleValidate(JsonElement root)
    {
        string manifestJson = root.TryGetProperty("manifestJson", out JsonElement jEl) && jEl.ValueKind == JsonValueKind.String
            ? jEl.GetString() ?? string.Empty
            : string.Empty;
        string? logicJs = root.TryGetProperty("logicJs", out JsonElement ljEl) && ljEl.ValueKind == JsonValueKind.String
            ? ljEl.GetString()
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
        string syncedManifestJson = !string.IsNullOrWhiteSpace(logicJs)
            ? CustomWidgetTemplates.SyncManifestScriptPropertyJson(manifestJson, logicJs)
            : manifestJson;
        CustomWidgetParseResult result = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            syncedManifestJson,
            logicJs,
            locales).ToParseResult();

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
            writer.WriteString("activeLocale", manager.ActiveCultureName);
            writer.WriteStartArray("widgets");
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("widgetId", item.Info.Id);
                writer.WriteString("name", item.Info.Name);
                writer.WriteBoolean("enabled", item.Info.IsEnabled);
                writer.WriteString("manifestId", item.Manifest?.Id ?? string.Empty);
                writer.WriteString("origin", item.Manifest?.Origin ?? CustomWidgetOriginKinds.Local);
                writer.WriteBoolean("isCustomized", item.Manifest?.IsCustomized == true);
                if (!string.IsNullOrWhiteSpace(item.Manifest?.ForkedFromId))
                {
                    writer.WriteString("forkedFromId", item.Manifest!.ForkedFromId);
                }
                if (!string.IsNullOrWhiteSpace(item.Manifest?.ForkedFromVersion))
                {
                    writer.WriteString("forkedFromVersion", item.Manifest!.ForkedFromVersion);
                }
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
        CustomWidgetManifest? parsedManifest = CustomWidgetManifestParser.Parse(manifestJson).Manifest;
        string? logicJs = await store.ReadLogicJsOrNullAsync();
        CustomWidgetLocaleCatalog? locales = await store.ReadLocaleCatalogAsync();
        CustomWidgetLiveRuntimeSnapshot? liveSnapshot = await manager.TryGetCustomWidgetLiveSnapshotAsync(config.Id);
        CustomWidgetStorePayload? diskPayload = liveSnapshot is null ? await store.LoadAsync() : null;

        return BuildSuccessJson(writer =>
        {
            writer.WriteString("widgetId", config.Id);
            writer.WriteString("name", config.Name);
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
            writer.WriteBoolean("visible", config.IsVisible && !config.IsDisabled);
            writer.WriteBoolean("liveLoaded", liveSnapshot?.LiveLoaded == true);
            writer.WriteString("displayMode", liveSnapshot?.DisplayMode ?? "offline");
            writer.WriteString("directory", store.WidgetDirectory);
            writer.WriteString("manifestPath", store.ManifestFilePath);
            writer.WriteString("manifestJson", manifestJson);
            writer.WriteBoolean("hasLogicJs", !string.IsNullOrWhiteSpace(logicJs));
            if (!string.IsNullOrWhiteSpace(logicJs))
            {
                writer.WriteString("logicPath", store.LogicJsPath);
                writer.WriteString("logicJs", logicJs);
            }
            bool hasLocales = locales is { HasAnyLocale: true };
            writer.WriteBoolean("hasLocales", hasLocales);
            if (hasLocales)
            {
                writer.WriteString("localesDir", store.LocalesDirectoryPath);
                writer.WritePropertyName("locales");
                locales!.WriteCatalogObjectTo(writer);
            }

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
    private async Task<string> HandleStageCommitAsync(JsonElement root)
    {
        string? culture = _widgetManagerProvider()?.ActiveCultureName;
        if (!CustomWidgetAgentStageStore.TryReadBundle(ReadStageId(root), out AgentStageBundle? bundle, out string error, culture) ||
            bundle is null)
        {
            return BuildErrorJson(error);
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("op", "upsert");
            writer.WriteBoolean("commit", true);
            writer.WriteString("stageId", bundle.StageId);
            if (!string.IsNullOrWhiteSpace(bundle.WidgetId))
            {
                writer.WriteString("widgetId", bundle.WidgetId);
            }
            writer.WriteString("manifestJson", bundle.ManifestJson);
            if (bundle.HasLogicJs)
            {
                writer.WriteString("logicJs", bundle.LogicJs ?? string.Empty);
            }
            if (bundle.HasLocales)
            {
                if (bundle.LocalesJson is null)
                {
                    writer.WriteNull("locales");
                }
                else
                {
                    writer.WritePropertyName("locales");
                    writer.WriteRawValue(bundle.LocalesJson, skipInputValidation: true);
                }
            }
            if (bundle.ResizeToDefault)
            {
                writer.WriteBoolean("resizeToDefault", true);
            }
            writer.WriteEndObject();
        }

        using JsonDocument doc = JsonDocument.Parse(buffer.ToArray());
        return await HandleUpsertAsync(doc.RootElement);
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

    private async Task<string> HandleUpsertAsync(JsonElement root)
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
        string manifestJson = root.TryGetProperty("manifestJson", out JsonElement mEl) && mEl.ValueKind == JsonValueKind.String
            ? mEl.GetString() ?? string.Empty
            : string.Empty;
        bool hasCallerResize = root.TryGetProperty("resizeToDefault", out JsonElement rEl) &&
                               rEl.ValueKind is JsonValueKind.True or JsonValueKind.False;
        bool resizeToDefault = hasCallerResize && rEl.ValueKind == JsonValueKind.True;
        // P2：只有显式 reveal=true 才把窗口带到前台，默认不再自动改变桌面呈现。
        bool reveal = root.TryGetProperty("reveal", out JsonElement revEl) && revEl.ValueKind == JsonValueKind.True;
        // P2：外部写入默认只进入暂存区；commit=true 且携带有效 stageId 才落盘。
        bool commit = root.TryGetProperty("commit", out JsonElement commitEl) && commitEl.ValueKind == JsonValueKind.True;
        string? stageId = root.TryGetProperty("stageId", out JsonElement stageEl) && stageEl.ValueKind == JsonValueKind.String
            ? stageEl.GetString()
            : null;

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

        // commit=true 时若调用方按协议只传 stageId + manifestJson，提前从暂存包回填 logicJs / locales / resizeToDefault，
        // 既避免校验阶段丢失已暂存的脚本/语言包，又避免落盘时把已暂存的 logic.js 与 locales 覆盖清空。
        if (commit && !string.IsNullOrWhiteSpace(stageId))
        {
            if (CustomWidgetAgentStageStore.TryReadBundle(stageId, out AgentStageBundle? stagedPreview, out _, activeCulture) &&
                stagedPreview is not null)
            {
                if (string.IsNullOrWhiteSpace(widgetId) && !string.IsNullOrWhiteSpace(stagedPreview.WidgetId))
                {
                    widgetId = stagedPreview.WidgetId;
                }

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
            else if (string.IsNullOrWhiteSpace(widgetId) &&
                     CustomWidgetAgentStageStore.TryGetCommitted(stageId, out string? prevCommittedId, manifestJson) &&
                     !string.IsNullOrWhiteSpace(prevCommittedId))
            {
                widgetId = prevCommittedId;
            }
        }

        string effectiveManifestJson = updateLogicJs
            ? CustomWidgetTemplates.SyncManifestScriptPropertyJson(manifestJson, logicJs)
            : manifestJson;

        WidgetConfig? existingTarget = !string.IsNullOrWhiteSpace(widgetId)
            ? manager.ResolveCustomWidgetConfig(widgetId)
            : null;
        string? validationLogicJs = updateLogicJs
            ? logicJs
            : (existingTarget is not null
                ? await CustomWidgetDataStore.ForWidget(existingTarget.Id).ReadLogicJsOrNullAsync()
                : null);
        CustomWidgetLocaleCatalog? validationLocales = updateLocales
            ? locales
            : (existingTarget is not null
                ? await CustomWidgetDataStore.ForWidget(existingTarget.Id).ReadLocaleCatalogAsync()
                : null);

        CustomWidgetParseResult validation = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            effectiveManifestJson,
            validationLogicJs,
            validationLocales).ToParseResult();
        if (validation.Manifest is null || validation.Diagnostics.Any(static d => d.IsError))
        {
            string firstErr = validation.Diagnostics.FirstOrDefault(static d => d.IsError)?.Message ?? "Invalid manifest.json";
            return BuildErrorJson(firstErr);
        }

        // P2 默认路径：校验通过后只登记暂存批次，绝不写工作区、绝不改桌面配置。
        if (!commit)
        {
            string newStageId = CustomWidgetAgentStageStore.Stage(new AgentStageRequest(
                widgetId,
                manifestJson,
                logicJs,
                updateLogicJs,
                localesRaw,
                updateLocales,
                resizeToDefault));
            return BuildSuccessJson(writer =>
            {
                writer.WriteBoolean("staged", true);
                writer.WriteBoolean("committed", false);
                writer.WriteString("stageId", newStageId);
                if (!string.IsNullOrWhiteSpace(widgetId))
                {
                    writer.WriteString("widgetId", widgetId);
                }
                writer.WriteString("next",
                    "Validated and staged in DeskBox Workshop (desktop unchanged). Share this stageId with the user; after explicit user confirmation, call deskbox_upsert_widget again with commit=true, the same stageId, and the exact same manifestJson.");
                writer.WriteStartArray("warnings");
                foreach (CustomWidgetValidationDiagnostic warn in validation.Diagnostics.Where(static d => !d.IsError))
                {
                    writer.WriteStringValue($"{warn.Path}: {warn.Message}");
                }
                writer.WriteEndArray();
            });
        }

        // P2 提交路径：stageId 必须来自此前一次暂存，且全包内容摘要必须一致。
        if (!CustomWidgetAgentStageStore.TryConsumeBundle(
                stageId,
                manifestJson,
                hasCallerLogicJs,
                callerLogicJs,
                hasCallerLocales,
                callerLocalesRaw,
                out AgentStageBundle? consumedBundle,
                out string stageError,
                activeCulture))
        {
            // 幂等：同一批次被重复提交（客户端重试、Agent 重放）时不应报「stageId 不存在」。
            if (!hasCallerLogicJs &&
                !hasCallerLocales &&
                CustomWidgetAgentStageStore.TryGetCommitted(stageId, out string? committedWidgetId, manifestJson))
            {
                string idempotentWidgetId = !string.IsNullOrWhiteSpace(committedWidgetId)
                    ? committedWidgetId!
                    : validation.Manifest.Id;
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

        if (consumedBundle is not null)
        {
            widgetId ??= consumedBundle.WidgetId;
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

        if (string.IsNullOrWhiteSpace(widgetId))
        {
            // 目标必须显式：优先用 manifest.id 作为稳定标识，不依赖当前打开的工坊窗口。
            widgetId = validation.Manifest.Id;
        }

        GlanceWidgetInstanceInfo info = await manager.UpsertCustomWidgetFromAgentAsync(
            widgetId,
            manifestJson,
            resizeToDefault,
            reveal,
            logicJs,
            updateLogicJs,
            locales,
            updateLocales);
        CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(info.Id);
        // 用真实落盘 id 覆写幂等记录：暂存时 widgetId 可能为空，最终 id 由 manifest.id 决定。
        CustomWidgetAgentStageStore.RecordCommitted(stageId, info.Id);

        return BuildSuccessJson(writer =>
        {
            writer.WriteString("widgetId", info.Id);
            writer.WriteString("name", info.Name);
            writer.WriteBoolean("committed", true);
            writer.WriteBoolean("staged", false);
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
