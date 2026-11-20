using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Services.Studio;

/// <summary>
/// Main-process request router for <see cref="StudioIpcMethods"/>. Accepts pure
/// delegates so it stays completely decoupled from global application state and
/// can be tested directly over a loopback pipe.
/// </summary>
internal sealed class StudioIpcRouter
{
    private readonly SettingsService _settingsService;
    private readonly Func<IStudioHostBridge> _getInProcessBridge;
    private readonly Func<ConnectedMcpClientInfo?> _getLastMcpClient;
    private readonly Func<(StudioShowIntentPayload? Show, StudioShowPreviewIntentPayload? Preview)> _consumeInitialBootstrapIntents;
    private readonly Action _onHostBootReady;
    private readonly Action<string?> _onHostFarewell;
    private readonly Action<long, string?> _onWindowStateReported;
    private readonly Action<StudioContextReportPayload> _onStudioContextReported;
    private readonly Action<StudioWindowStatePayload>? _onSequencedWindowStateReported;
    private readonly Action<StudioStagePngCaptureReport>? _onStagePngReported;

    public StudioIpcRouter(
        SettingsService settingsService,
        Func<IStudioHostBridge> getInProcessBridge,
        Func<ConnectedMcpClientInfo?> getLastMcpClient,
        Func<(StudioShowIntentPayload? Show, StudioShowPreviewIntentPayload? Preview)> consumeInitialBootstrapIntents,
        Action onHostBootReady,
        Action<string?> onHostFarewell,
        Action<long, string?> onWindowStateReported,
        Action<StudioContextReportPayload> onStudioContextReported,
        Action<StudioWindowStatePayload>? onSequencedWindowStateReported = null,
        Action<StudioStagePngCaptureReport>? onStagePngReported = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _getInProcessBridge = getInProcessBridge ?? throw new ArgumentNullException(nameof(getInProcessBridge));
        _getLastMcpClient = getLastMcpClient ?? throw new ArgumentNullException(nameof(getLastMcpClient));
        _consumeInitialBootstrapIntents = consumeInitialBootstrapIntents ?? throw new ArgumentNullException(nameof(consumeInitialBootstrapIntents));
        _onHostBootReady = onHostBootReady ?? throw new ArgumentNullException(nameof(onHostBootReady));
        _onHostFarewell = onHostFarewell ?? throw new ArgumentNullException(nameof(onHostFarewell));
        _onWindowStateReported = onWindowStateReported ?? throw new ArgumentNullException(nameof(onWindowStateReported));
        _onStudioContextReported = onStudioContextReported ?? throw new ArgumentNullException(nameof(onStudioContextReported));
        _onSequencedWindowStateReported = onSequencedWindowStateReported;
        _onStagePngReported = onStagePngReported;
    }

    public async Task<(bool Ok, string? Payload, string? Error)> DispatchAsync(
        string method,
        string? payload,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            switch (method)
            {
                case StudioIpcMethods.Bootstrap:
                {
                    (StudioShowIntentPayload? showIntent, StudioShowPreviewIntentPayload? previewIntent) =
                        _consumeInitialBootstrapIntents();
                    var bootstrap = new StudioBootstrapResponse(
                        SettingsSnapshotJson: _settingsService.SerializeCurrentSettings(),
                        LastMcpClient: _getLastMcpClient(),
                        InitialShowIntent: showIntent,
                        InitialPreviewIntent: previewIntent);
                    return (
                        true,
                        SettingsIpcPayload.Encode(bootstrap, SettingsIpcJsonContext.Default.StudioBootstrapResponse),
                        null);
                }

                case StudioIpcMethods.HostBootReady:
                    _onHostBootReady();
                    return (true, null, null);

                case StudioIpcMethods.HostFarewell:
                    _onHostFarewell(payload);
                    return (true, null, null);

                case StudioIpcMethods.ReportWindowState:
                {
                    StudioWindowStatePayload? state = !string.IsNullOrWhiteSpace(payload)
                        ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.StudioWindowStatePayload)
                        : null;
                    if (state is null)
                    {
                        return (false, null, "Invalid StudioWindowStatePayload.");
                    }

                    if (_onSequencedWindowStateReported is not null)
                    {
                        _onSequencedWindowStateReported(state);
                    }
                    else
                    {
                        _onWindowStateReported(state.WindowHandle, state.TargetWidgetId);
                    }

                    return (true, null, null);
                }

                case StudioIpcMethods.ReportStudioContext:
                {
                    StudioContextReportPayload? report = !string.IsNullOrWhiteSpace(payload)
                        ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.StudioContextReportPayload)
                        : null;
                    if (report is null)
                    {
                        return (false, null, "Invalid StudioContextReportPayload.");
                    }

                    _onStudioContextReported(report);
                    return (true, null, null);
                }

                case StudioIpcMethods.ReportStagePng:
                {
                    if (!StudioStagePngWireHelper.TryDecodeReport(payload, out StudioStagePngCaptureReport? pngReport) ||
                        pngReport is null)
                    {
                        return (false, null, "Invalid StudioStagePngCaptureReport.");
                    }

                    _onStagePngReported?.Invoke(pngReport);
                    return (true, null, null);
                }

                case StudioIpcMethods.BringToFront:
                {
                    StudioWindowBandPayload? band = !string.IsNullOrWhiteSpace(payload)
                        ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.StudioWindowBandPayload)
                        : null;
                    if (band is not null && band.WindowHandle != 0)
                    {
                        _getInProcessBridge().BringAuxiliaryWindowToFront(
                            new IntPtr(band.WindowHandle),
                            band.Reason);
                    }

                    return (true, null, null);
                }

                case StudioIpcMethods.ReleaseBand:
                {
                    StudioWindowBandPayload? band = !string.IsNullOrWhiteSpace(payload)
                        ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.StudioWindowBandPayload)
                        : null;
                    if (band is not null && band.WindowHandle != 0)
                    {
                        _getInProcessBridge().ReleaseRaisedBandGuest(
                            new IntPtr(band.WindowHandle),
                            band.Reason);
                    }

                    return (true, null, null);
                }

                case StudioIpcMethods.CreateWidget:
                {
                    StudioCreateWidgetRequest? req = !string.IsNullOrWhiteSpace(payload)
                        ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.StudioCreateWidgetRequest)
                        : null;
                    if (req is null)
                    {
                        return (false, null, "Invalid StudioCreateWidgetRequest.");
                    }

                    CustomWidgetLocaleCatalog? locales = StudioLocaleWireHelper.Deserialize(
                        req.LocalesJsonOverride,
                        req.HasLocalesOverride);
                    GlanceWidgetInstanceInfo? created = await _getInProcessBridge().CreateCustomWidgetAsync(
                        req.TemplateOrPackageSpec,
                        logicJsOverride: req.LogicJsOverride,
                        hasLogicJsOverride: req.HasLogicJsOverride,
                        localesOverride: locales,
                        hasLocalesOverride: req.HasLocalesOverride,
                        initialSettings: req.InitialSettings,
                        initialSecrets: req.InitialSecrets);
                    return OkMutation(created);
                }

                case StudioIpcMethods.UpdateWidgetManifest:
                {
                    StudioUpdateManifestRequest? req = !string.IsNullOrWhiteSpace(payload)
                        ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.StudioUpdateManifestRequest)
                        : null;
                    if (req is null || string.IsNullOrWhiteSpace(req.WidgetId))
                    {
                        return (false, null, "Invalid StudioUpdateManifestRequest.");
                    }

                    CustomWidgetLocaleCatalog? locales = StudioLocaleWireHelper.Deserialize(
                        req.LocalesJson,
                        req.HasLocales);
                    GlanceWidgetInstanceInfo? updated = await _getInProcessBridge().UpdateCustomWidgetManifestAsync(
                        req.WidgetId,
                        req.ManifestJson,
                        resizeToDefault: req.ResizeToDefault,
                        reveal: req.Reveal,
                        logicJs: req.LogicJs,
                        updateLogicJs: req.UpdateLogicJs,
                        locales: locales,
                        updateLocales: req.UpdateLocales);
                    return OkMutation(updated);
                }

                case StudioIpcMethods.ApplyMutation:
                {
                    CustomWidgetSettingsMutation? mutation = !string.IsNullOrWhiteSpace(payload)
                        ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.CustomWidgetSettingsMutation)
                        : null;
                    if (mutation is null)
                    {
                        return (false, null, "Invalid CustomWidgetSettingsMutation.");
                    }

                    await _getInProcessBridge().ApplyCustomWidgetMutationAsync(mutation);
                    return OkMutation(null);
                }

                case StudioIpcMethods.ApplyStoreData:
                    return await CustomWidgetDataStore.ApplyRelayedStorePayloadAsync(payload);

                case StudioIpcMethods.SetWidgetEnabled:
                {
                    StudioSetWidgetEnabledRequest? req = !string.IsNullOrWhiteSpace(payload)
                        ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.StudioSetWidgetEnabledRequest)
                        : null;
                    if (req is null || string.IsNullOrWhiteSpace(req.WidgetId))
                    {
                        return (false, null, "Invalid StudioSetWidgetEnabledRequest.");
                    }

                    await _getInProcessBridge().SetCustomWidgetInstanceEnabledAsync(req.WidgetId, req.Enabled);
                    return OkMutation(null);
                }

                case StudioIpcMethods.LocateWidget:
                {
                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        return (false, null, "WidgetId is required.");
                    }

                    await _getInProcessBridge().LocateCustomWidgetAsync(payload);
                    return (true, null, null);
                }

                case StudioIpcMethods.RemoveWidget:
                {
                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        return (false, null, "WidgetId is required.");
                    }

                    await _getInProcessBridge().RemoveCustomWidgetAsync(payload);
                    return OkMutation(null);
                }

                case StudioIpcMethods.ShowNotification:
                {
                    StudioNotificationPayload? notify = !string.IsNullOrWhiteSpace(payload)
                        ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.StudioNotificationPayload)
                        : null;
                    if (notify is not null)
                    {
                        _getInProcessBridge().ShowNotification(notify.Title, notify.Body);
                    }

                    return (true, null, null);
                }

                case StudioIpcMethods.CommitAgentStage:
                {
                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        return (false, null, "StageId is required.");
                    }

                    string responseJson = await _getInProcessBridge().CommitAgentStageAsync(payload);
                    var response = new StudioStageCommitResponse(
                        ResponseJson: responseJson,
                        LayoutSliceJson: _settingsService.SerializeCurrentLayout());
                    return (
                        true,
                        SettingsIpcPayload.Encode(response, SettingsIpcJsonContext.Default.StudioStageCommitResponse),
                        null);
                }

                case StudioIpcMethods.DiscardAgentStage:
                {
                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        return (false, null, "StageId is required.");
                    }

                    await _getInProcessBridge().DiscardAgentStageAsync(payload);
                    return (true, null, null);
                }

                default:
                    return (false, null, $"Unknown Studio IPC method '{method}'.");
            }
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private (bool Ok, string? Payload, string? Error) OkMutation(GlanceWidgetInstanceInfo? instance)
    {
        var response = new StudioWidgetMutationResponse(
            Instance: instance,
            LayoutSliceJson: _settingsService.SerializeCurrentLayout());
        return (
            true,
            SettingsIpcPayload.Encode(response, SettingsIpcJsonContext.Default.StudioWidgetMutationResponse),
            null);
    }
}
