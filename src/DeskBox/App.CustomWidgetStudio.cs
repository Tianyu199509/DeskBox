using DeskBox.FileSafety;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Studio;

namespace DeskBox;

public partial class App
{
    private IStudioHostBridge? _inProcessStudioHostBridge;
    internal async Task HandleProtocolActivationUriAsync(string rawUri)
    {
        if (IsShuttingDown || string.IsNullOrWhiteSpace(rawUri))
        {
            return;
        }

        if (!DeskBoxProtocolActivationService.TryParseProtocolUri(rawUri, out DeskBoxProtocolIntent? intent, out string? error) ||
            intent is null)
        {
            Log($"[Protocol] Rejected deskbox:// URI '{rawUri}': {error}");
            return;
        }

        if (intent.Action == DeskBoxProtocolAction.BackgroundStartup)
        {
            return;
        }

        if (intent.Action == DeskBoxProtocolAction.OpenStudio)
        {
            ShowCustomWidgetStudio(intent.WidgetId, intent.Idea, autoStartGeneration: false);
            return;
        }

        if (intent.Action == DeskBoxProtocolAction.InstallWidget)
        {
            DeskBoxProtocolResolvedBundle resolved =
                await DeskBoxProtocolActivationService.ResolveInstallWidgetBundleAsync(intent);
            if (!string.IsNullOrWhiteSpace(resolved.ManifestJson))
            {
                ShowCustomWidgetStudioPreview(
                    resolved.ManifestJson,
                    resolved.DisplayLabel,
                    resolved.LogicJs,
                    resolved.Locales,
                    resolved.OriginKind);
            }
            else
            {
                Log($"[Protocol] install-widget failed for '{rawUri}': {resolved.ErrorMessage}");
                _nativeNotificationService?.TryShow(
                    LocalizationService.T("CustomWidget.Title"),
                    resolved.ErrorMessage ?? LocalizationService.T("CustomWidget.ClientUpgrade.Subtitle"));
            }
        }
    }

    /// <summary>
    /// Opens the standalone Widget Workshop. <paramref name="widgetId"/> edits an
    /// existing custom widget with AI; <paramref name="initialIdea"/> starts
    /// generating right away from a sentence typed elsewhere.
    /// </summary>
    public void ShowCustomWidgetStudio(
        string? widgetId = null,
        string? initialIdea = null,
        bool autoStartGeneration = true)
    {
        if (IsShuttingDown)
        {
            return;
        }

        ShowStudioViaHost(widgetId, initialIdea, autoStartGeneration);
    }

    public void ShowCustomWidgetStudioPreview(
        string manifestJson,
        string? sourceLabel = null,
        string? logicJs = null,
        CustomWidgetLocaleCatalog? locales = null,
        string? originKind = null)
    {
        if (IsShuttingDown || string.IsNullOrWhiteSpace(manifestJson))
        {
            return;
        }

        ShowStudioPreviewViaHost(manifestJson, sourceLabel, logicJs, locales, originKind);
    }

    internal string? TryGetStudioTargetWidgetIdForAgentUpsert() =>
        IsStudioHostRunning ? _studioHostTargetWidgetId : null;

    internal async Task<string> BuildStudioContextResponseJsonAsync(bool includeManifest = true)
    {
        if (!IsShuttingDown &&
            await TryQueryRemoteStudioContextJsonAsync(includeManifest, TimeSpan.FromSeconds(2)) is { } remoteJson)
        {
            return remoteJson;
        }

        AppSettings? s = SettingsService?.Settings;
        return DeskBoxAgentBridgeServer.BuildHeadlessStudioContextResponseJson(
            LocalizationService?.CurrentCultureName,
            s is not null && double.IsFinite(s.WidgetShell.DefaultWidgetWidth) ? (int)Math.Round(s.WidgetShell.DefaultWidgetWidth) : null,
            s is not null && double.IsFinite(s.WidgetShell.DefaultWidgetHeight) ? (int)Math.Round(s.WidgetShell.DefaultWidgetHeight) : null);
    }

    /// <summary>Cached main-process bridge used by <see cref="StudioIpcRouter"/>.</summary>
    private IStudioHostBridge GetStudioHostBridge() =>
        _inProcessStudioHostBridge ??= new InProcessStudioHostBridge(
            getWidgetManager: () => WidgetManager,
            getNotificationService: () => NativeNotificationService,
            commitAgentStageAsync: CommitAgentStageAsync,
            discardAgentStageAsync: DiscardAgentStageAsync);

    internal IReadOnlyList<AgentStageSummary> GetPendingAgentStages() =>
        CustomWidgetAgentStageStore.ListPending();

    internal bool TryReadAgentStage(string stageId, out AgentStageBundle? bundle, out string error) =>
        CustomWidgetAgentStageStore.TryReadBundle(stageId, out bundle, out error);

    // stageId 一律来自 ListPending / TryReadBundle，并在拼接请求前就地复核格式。
    private static bool IsWellFormedStageId(string? stageId) =>
        !string.IsNullOrWhiteSpace(stageId) &&
        stageId.Length == 32 &&
        stageId.All(static ch => char.IsAsciiHexDigit(ch));

    /// <summary>用户点「放到桌面」后，走 Bridge 的 stage_commit，复用同一条落盘路径。</summary>
    internal Task<string> CommitAgentStageAsync(string stageId) =>
        DispatchAgentStageOpAsync("stage_commit", stageId);

    /// <summary>用户点「忽略」后，丢弃该暂存批次，桌面不变。</summary>
    internal Task<string> DiscardAgentStageAsync(string stageId) =>
        DispatchAgentStageOpAsync("stage_discard", stageId);

    private async Task<string> DispatchAgentStageOpAsync(string op, string stageId)
    {
        if (!IsWellFormedStageId(stageId))
        {
            return "{\"ok\":false,\"error\":\"Invalid stageId.\"}";
        }

        if (_agentBridgeServer is null)
        {
            return "{\"ok\":false,\"error\":\"Agent bridge is not running.\"}";
        }

        // op 为内部常量、stageId 已复核为 32 位十六进制，拼接无转义风险。
        string request = "{\"op\":\"" + op + "\",\"stageId\":\"" + stageId + "\"}";
        return await _agentBridgeServer.DispatchHostUiRequestJsonAsync(request);
    }

    internal void NotifyCustomWidgetStudioAgentUpsert(
        string widgetId,
        string widgetName,
        string manifestJson,
        string? logicJs,
        bool hasLogicJs,
        CustomWidgetLocaleCatalog? locales,
        bool hasLocales)
    {
        if (IsShuttingDown)
        {
            return;
        }

        if (IsStudioHostRunning && HasConnectedStudioIpcClient)
        {
            string payload = SettingsIpcPayload.Encode(
                new StudioAgentUpsertEventPayload(
                    WidgetId: widgetId,
                    WidgetName: widgetName,
                    ManifestJson: manifestJson,
                    LogicJs: logicJs,
                    HasLogicJs: hasLogicJs,
                    LocalesJson: StudioLocaleWireHelper.Serialize(locales),
                    HasLocales: hasLocales && locales is { HasAnyLocale: true }),
                SettingsIpcJsonContext.Default.StudioAgentUpsertEventPayload);
            _ = PushStudioIpcEventAsync(StudioIpcMethods.AgentWidgetUpserted, payload);
        }
    }

    internal void ConfigureCustomWidgetHostActionBridge()
    {
        CustomWidgetHostActionBridge.OpenSearchPopupHandler = query =>
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                OpenSearchPopup();
            }
            else
            {
                OpenSearchPopupWithQuery(query);
            }
        };
        CustomWidgetHostActionBridge.ShowDesktopToastHandler = request =>
            DesktopToastHudService.Shared.TryShow(request, SettingsService);
        CustomWidgetHostActionBridge.RecycleEntryHandler = (path, ownerHwnd) =>
            FileService.DeleteEntryAsync(path, recycle: true, ownerHandle: ownerHwnd);
    }
}
