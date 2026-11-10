using DeskBox.FileSafety;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox;

public partial class App
{
    private CustomWidgetStudioWindow? _customWidgetStudioWindow;

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

        CancelBackgroundMemoryCleanup();
        bool isForkRequest = !string.IsNullOrWhiteSpace(widgetId) &&
                             widgetId.Trim().StartsWith("fork:", StringComparison.OrdinalIgnoreCase);
        string? forkSourceId = isForkRequest ? widgetId!.Trim()["fork:".Length..].Trim() : null;

        if (_customWidgetStudioWindow is null)
        {
            EnsureXamlLoopLifelineIfNoOtherWindows();
            var window = new CustomWidgetStudioWindow(LocalizationService);
            _customWidgetStudioWindow = window;
            ThemeService.TrackWindow(window);
            window.Closed += CustomWidgetStudioWindow_Closed;
            if (isForkRequest && !string.IsNullOrWhiteSpace(forkSourceId))
            {
                window.InitializeForkFromWidget(forkSourceId, initialIdea, autoStartGeneration);
            }
            else
            {
                window.Initialize(widgetId, initialIdea, autoStartGeneration);
            }
        }
        else if (isForkRequest && !string.IsNullOrWhiteSpace(forkSourceId))
        {
            _customWidgetStudioWindow.InitializeForkFromWidget(forkSourceId, initialIdea, autoStartGeneration);
        }
        else
        {
            _customWidgetStudioWindow.RequestTarget(widgetId, initialIdea, autoStartGeneration);
        }

        _customWidgetStudioWindow.ShowWindow();
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

        CancelBackgroundMemoryCleanup();
        if (_customWidgetStudioWindow is null)
        {
            EnsureXamlLoopLifelineIfNoOtherWindows();
            var window = new CustomWidgetStudioWindow(LocalizationService);
            _customWidgetStudioWindow = window;
            ThemeService.TrackWindow(window);
            window.Closed += CustomWidgetStudioWindow_Closed;
            window.Initialize(null, null);
        }

        _customWidgetStudioWindow.OpenExternalManifestPreview(
            manifestJson,
            sourceLabel,
            logicJs,
            locales,
            originKind);
        _customWidgetStudioWindow.ShowWindow();
    }

    internal string? TryGetStudioTargetWidgetIdForAgentUpsert() =>
        _customWidgetStudioWindow?.TryGetTargetWidgetIdForAgentUpsert();

    internal async Task<string> BuildStudioContextResponseJsonAsync(bool includeManifest = true)
    {
        CustomWidgetStudioWindow? studio = _customWidgetStudioWindow;
        if (IsShuttingDown || studio is null)
        {
            return DeskBoxAgentBridgeServer.BuildHeadlessStudioContextResponseJson(
                LocalizationService?.CurrentCultureName);
        }

        if (studio.DispatcherQueue.HasThreadAccess)
        {
            return DeskBoxAgentBridgeServer.BuildSuccessJson(
                writer => studio.WriteStudioContextTo(writer, includeManifest));
        }

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!studio.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    string json = DeskBoxAgentBridgeServer.BuildSuccessJson(
                        writer => studio.WriteStudioContextTo(writer, includeManifest));
                    tcs.TrySetResult(json);
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(DeskBoxAgentBridgeServer.BuildErrorJson(ex.Message));
                }
            }))
        {
            return DeskBoxAgentBridgeServer.BuildHeadlessStudioContextResponseJson(
                LocalizationService?.CurrentCultureName);
        }

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            return DeskBoxAgentBridgeServer.BuildHeadlessStudioContextResponseJson(
                LocalizationService?.CurrentCultureName);
        }
    }

    /// <summary>供工坊首屏列出「来自外部工具、待用户确认」的暂存批次。</summary>
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
        return await _agentBridgeServer.DispatchRequestJsonAsync(request);
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
        if (IsShuttingDown || _customWidgetStudioWindow is null)
        {
            return;
        }

        _customWidgetStudioWindow.OnExternalAgentWidgetUpserted(
            widgetId,
            widgetName,
            manifestJson,
            logicJs,
            hasLogicJs,
            locales,
            hasLocales);
    }

    private void CustomWidgetStudioWindow_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        if (sender is not CustomWidgetStudioWindow window)
        {
            return;
        }

        window.Closed -= CustomWidgetStudioWindow_Closed;
        if (ReferenceEquals(_customWidgetStudioWindow, window))
        {
            _customWidgetStudioWindow = null;
        }

        ScheduleLightMemoryCleanup(completedHeavyOperation: true);
        ScheduleBackgroundMemoryCleanup("custom-widget-studio-closed");
    }
}
