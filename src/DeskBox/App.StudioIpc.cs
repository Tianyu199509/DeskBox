using System.Collections.Concurrent;
using DeskBox.FileSafety;
using DeskBox.Services;
using DeskBox.Services.Studio;

namespace DeskBox;

/// <summary>
/// Main-process side of the Custom Widget Studio IPC pipe (<see cref="DeskBoxDataPathService.StudioIpcPipeName"/>).
/// Owns the server instance, request routing, live Studio context queries for external MCP clients,
/// and event pushes (layout/settings updates, display topology, MCP telemetry, AgentStage updates).
/// </summary>
public partial class App
{
    private SettingsIpcServer? _studioIpcServer;
    private StudioIpcRouter? _studioIpcRouter;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingStudioContextQueries =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(byte[] PngBytes, int Width, int Height, int OverflowPx)?>> _pendingStudioPreviewCaptures =
        new(StringComparer.Ordinal);

    private bool HasConnectedStudioIpcClient =>
        _studioIpcServer is { HasConnectedClient: true };

    private void StartStudioIpcServer()
    {
        if (_studioIpcServer is not null)
        {
            return;
        }

        var router = new StudioIpcRouter(
            settingsService: SettingsService,
            getInProcessBridge: GetStudioHostBridge,
            getLastMcpClient: static () => DeskBoxAgentBridgeServer.LastMcpClient,
            consumeInitialBootstrapIntents: ConsumeInitialStudioBootstrapIntents,
            onHostBootReady: OnStudioHostBootReady,
            onHostFarewell: payload =>
            {
                if (uint.TryParse(payload, out uint senderPid))
                {
                    _studioIpcServer?.SuppressEventsForExitingClient();
                    MarkStudioHostExiting(senderPid);
                }
            },
            onWindowStateReported: UpdateStudioHostWindowState,
            onStudioContextReported: OnStudioContextReportedFromHost,
            onSequencedWindowStateReported: state =>
                UpdateStudioHostWindowState(state.WindowHandle, state.TargetWidgetId, state.StateSequence),
            onStagePngReported: OnStudioStagePngReportedFromHost);

        _studioIpcRouter = router;
        _studioIpcServer = SettingsIpcServer.Start(
            DeskBoxDataPathService.Current.StudioIpcPipeName,
            router.DispatchAsync,
            expectedClientIdentityProvider: () => StudioHostPipeClientIdentity,
            onClientDisconnected: FailAllPendingStudioContextQueries);

        SettingsService.LocalPersistCompleted += OnStudioIpcLocalPersistCompleted;
        SettingsService.LayoutPersistCompleted += OnStudioIpcLayoutPersistCompleted;
        DisplayTopologyChanged += OnStudioIpcDisplayTopologyChanged;
        DeskBoxAgentBridgeServer.McpClientConnected += OnStudioIpcMcpClientConnected;
        DeskBoxAgentBridgeServer.McpActivityRecorded += OnStudioIpcMcpActivityRecorded;
        CustomWidgetAgentStageStore.Changed += OnStudioIpcAgentStageChanged;

        Log("[StudioIpc] Server started");
    }

    private void ShutdownStudioIpcServer()
    {
        FailAllPendingStudioContextQueries();
        if (_studioIpcServer is null)
        {
            return;
        }

        SettingsService.LocalPersistCompleted -= OnStudioIpcLocalPersistCompleted;
        SettingsService.LayoutPersistCompleted -= OnStudioIpcLayoutPersistCompleted;
        DisplayTopologyChanged -= OnStudioIpcDisplayTopologyChanged;
        DeskBoxAgentBridgeServer.McpClientConnected -= OnStudioIpcMcpClientConnected;
        DeskBoxAgentBridgeServer.McpActivityRecorded -= OnStudioIpcMcpActivityRecorded;
        CustomWidgetAgentStageStore.Changed -= OnStudioIpcAgentStageChanged;

        SettingsIpcServer server = _studioIpcServer;
        _studioIpcServer = null;
        _studioIpcRouter = null;
        _ = server.DisposeAsync().AsTask();
    }

    private void OnStudioContextReportedFromHost(StudioContextReportPayload report)
    {
        _studioHostTargetWidgetId = string.IsNullOrWhiteSpace(report.TargetWidgetId)
            ? null
            : report.TargetWidgetId;
        if (_pendingStudioContextQueries.TryRemove(report.RequestId, out TaskCompletionSource<string>? tcs))
        {
            tcs.TrySetResult(report.ContextJson);
        }
    }

    private void OnStudioStagePngReportedFromHost(StudioStagePngCaptureReport report)
    {
        if (_pendingStudioPreviewCaptures.TryRemove(
                report.RequestId,
                out TaskCompletionSource<(byte[] PngBytes, int Width, int Height, int OverflowPx)?>? tcs))
        {
            tcs.TrySetResult(
                report is { Ok: true, PngBytes.Length: > 0 }
                    ? (report.PngBytes, report.Width, report.Height, report.OverflowPx)
                    : null);
        }
    }

    private void FailAllPendingStudioContextQueries()
    {
        foreach ((string key, _) in _pendingStudioContextQueries)
        {
            if (_pendingStudioContextQueries.TryRemove(key, out TaskCompletionSource<string>? tcs))
            {
                tcs.TrySetCanceled();
            }
        }

        foreach ((string key, _) in _pendingStudioPreviewCaptures)
        {
            if (_pendingStudioPreviewCaptures.TryRemove(
                    key,
                    out TaskCompletionSource<(byte[] PngBytes, int Width, int Height, int OverflowPx)?>? tcs))
            {
                tcs.TrySetCanceled();
            }
        }
    }

    internal Task<(byte[] PngBytes, int Width, int Height, int OverflowPx)?> TryCaptureRemoteStudioPreviewAsync(
        string? mode,
        TimeSpan? timeout = null) =>
        TryCaptureRemoteStudioPreviewAsync(
            string.Equals(mode?.Trim(), "capsule", StringComparison.OrdinalIgnoreCase),
            timeout);

    internal async Task<(byte[] PngBytes, int Width, int Height, int OverflowPx)?> TryCaptureRemoteStudioPreviewAsync(
        bool capsuleMode = false,
        TimeSpan? timeout = null)
    {
        if (IsShuttingDown || !IsStudioHostRunning || !HasConnectedStudioIpcClient)
        {
            return null;
        }

        lock (_pendingStudioHostIntentsGate)
        {
            if (!_studioHostBootConfirmed)
            {
                return null;
            }
        }

        string requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<(byte[] PngBytes, int Width, int Height, int OverflowPx)?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingStudioPreviewCaptures[requestId] = tcs;
        try
        {
            string reqPayload = StudioStagePngWireHelper.EncodeRequest(
                new StudioStagePngCaptureRequest(requestId, capsuleMode));
            await PushStudioIpcEventAsync(StudioIpcMethods.CaptureStudioStagePng, reqPayload);
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(3));
        }
        catch
        {
            return null;
        }
        finally
        {
            _pendingStudioPreviewCaptures.TryRemove(requestId, out _);
        }
    }

    internal async Task<string?> TryQueryRemoteStudioContextJsonAsync(bool includeManifest, TimeSpan timeout)
    {
        if (IsShuttingDown || !IsStudioHostRunning || !HasConnectedStudioIpcClient)
        {
            return null;
        }

        lock (_pendingStudioHostIntentsGate)
        {
            if (!_studioHostBootConfirmed)
            {
                return null;
            }
        }

        string requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingStudioContextQueries[requestId] = tcs;
        try
        {
            string reqPayload = SettingsIpcPayload.Encode(
                new StudioContextRequestPayload(requestId, includeManifest),
                SettingsIpcJsonContext.Default.StudioContextRequestPayload);
            await PushStudioIpcEventAsync(StudioIpcMethods.RequestStudioContext, reqPayload);
            return await tcs.Task.WaitAsync(timeout);
        }
        catch
        {
            return null;
        }
        finally
        {
            _pendingStudioContextQueries.TryRemove(requestId, out _);
        }
    }

    private void OnStudioIpcLayoutPersistCompleted()
    {
        if (!HasConnectedStudioIpcClient)
        {
            return;
        }

        _ = PushStudioIpcEventAsync(
            StudioIpcMethods.LayoutChanged,
            SettingsService.SerializeCurrentLayout());
    }

    private void OnStudioIpcLocalPersistCompleted(long mainCommitId)
    {
        if (!HasConnectedStudioIpcClient)
        {
            return;
        }

        _ = PushStudioIpcEventAsync(
            StudioIpcMethods.SettingsReloaded,
            SettingsIpcReloadPayload.Compose(mainCommitId, null));
    }

    private void OnStudioIpcDisplayTopologyChanged()
    {
        if (!HasConnectedStudioIpcClient)
        {
            return;
        }

        _ = PushStudioIpcEventAsync(StudioIpcMethods.DisplayTopologyChanged, null);
    }

    private void OnStudioIpcMcpClientConnected(ConnectedMcpClientInfo info)
    {
        if (!HasConnectedStudioIpcClient)
        {
            return;
        }

        string payload = SettingsIpcPayload.Encode(
            info,
            SettingsIpcJsonContext.Default.ConnectedMcpClientInfo);
        _ = PushStudioIpcEventAsync(StudioIpcMethods.McpClientConnected, payload);
    }

    private void OnStudioIpcMcpActivityRecorded(string op, string detail)
    {
        if (!HasConnectedStudioIpcClient)
        {
            return;
        }

        string payload = SettingsIpcPayload.Encode(
            new StudioMcpActivityPayload(op ?? string.Empty, detail ?? string.Empty),
            SettingsIpcJsonContext.Default.StudioMcpActivityPayload);
        _ = PushStudioIpcEventAsync(StudioIpcMethods.McpActivityRecorded, payload);
    }

    private void OnStudioIpcAgentStageChanged()
    {
        if (!HasConnectedStudioIpcClient)
        {
            return;
        }

        _ = PushStudioIpcEventAsync(StudioIpcMethods.AgentStageChanged, null);
    }

    private async Task PushStudioIpcEventAsync(string method, string? payload)
    {
        if (_studioIpcServer is not { } server)
        {
            return;
        }

        try
        {
            await server.SendEventAsync(method, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogVerbose($"[StudioIpc] Push '{method}' ignored: {ex.Message}");
        }
    }
}
