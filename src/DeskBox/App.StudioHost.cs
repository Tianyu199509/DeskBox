using System.Diagnostics;
using System.Globalization;
using DeskBox.Controls.Studio;
using DeskBox.FileSafety;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.Views;

namespace DeskBox;

/// <summary>
/// Out-of-process Custom Widget Studio companion host (<c>DeskBox.exe --studio &lt;mainPid&gt;</c>).
/// Mirrors the single-executable Settings host pattern (<see cref="SettingsHostLaunch"/>):
/// running the Workshop in its own companion process avoids duplicating the NativeAOT image on disk
/// while guaranteeing that closing the Workshop window terminates the process and immediately
/// returns 100% of its XAML visual tree, DirectComposition/GPU surfaces, JS/AI buffers, and
/// managed heap back to Windows.
/// </summary>
public partial class App
{
    internal const string StudioHostLaunchArgument = "--studio";

    internal static class StudioHostLaunch
    {
        internal const string Argument = StudioHostLaunchArgument;

        internal static bool IsStudioHostLaunch(string[] arguments)
        {
            for (int i = 1; i < arguments.Length; i++)
            {
                if (string.Equals(arguments[i], Argument, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static int TryGetMainProcessId(string[] arguments)
        {
            for (int i = 1; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], Argument, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(arguments[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) &&
                    pid > 0)
                {
                    return pid;
                }
            }

            return 0;
        }
    }

    private bool _isStudioHost;
    private SettingsIpcClient? _studioHostClient;
    private int _studioHostMainProcessId;
    private int _studioHostExitStarted;
    private long _studioHostWindowStateSequence;
    private CustomWidgetStudioWindow? _studioHostWindow;
    private RemoteStudioHostBridge? _studioHostBridge;
    private SettingsService? _studioHostSettingsService;
    private LocalizationService? _studioHostLocalizationService;
    private ThemeService? _studioHostThemeService;

    internal bool IsStudioHost => _isStudioHost;

    private void InitializeStudioHost(string[] effectiveCommandLineArgs)
    {
        _isStudioHost = true;
        _studioHostMainProcessId = StudioHostLaunch.TryGetMainProcessId(effectiveCommandLineArgs);
        JumpListService.RegisterProcessAppUserModelIdOnly();
        Log("[StudioHost] Constructor branch entered");
        InitializeComponent();
        UnhandledException += (_, eventArgs) =>
        {
            Log($"[StudioHost] Unhandled exception: {eventArgs.Message}");
            if (eventArgs.Exception is { } unhandled)
            {
                Log($"[StudioHost] Unhandled exception detail: {unhandled}");
            }

            eventArgs.Handled = true;
        };
    }

    private async void LaunchStudioHostAsync()
    {
        UiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (UiDispatcherQueue is not null)
        {
            DeskBox.Platform.UiDispatch.MarkReady(UiDispatcherQueue);
        }

        DeskBox.Helpers.ImeCompositionGuard.EnsureGlobalFocusTracking();
        if (_studioHostMainProcessId == 0)
        {
            string[] fallbackArgs = DeskBox.Platform.SingleInstanceAndActivationInterop.GetEffectiveLaunchCommandLineArgs(
                Environment.GetCommandLineArgs(),
                TryGetPackagedLaunchArguments());
            _studioHostMainProcessId = StudioHostLaunch.TryGetMainProcessId(fallbackArgs);
        }
        if (_studioHostMainProcessId == 0)
        {
            Log("[StudioHost] Missing main process id argument; exiting");
            ExitStudioHost();
            return;
        }

        SettingsIpcClient client;
        try
        {
            client = await SettingsIpcClient.ConnectAsync(
                DeskBoxDataPathService.Current.StudioIpcPipeName,
                TimeSpan.FromMilliseconds(500),
                attempts: 20);
        }
        catch (Exception ex)
        {
            Log(
                $"[StudioHost] IPC connect failed: {ex.GetType().FullName} " +
                $"(0x{ex.HResult:X8}): {ex.Message}");
            ExitStudioHost();
            return;
        }

        _studioHostClient = client;
        client.Disconnected += () =>
        {
            Log("[StudioHost] Pipe disconnected while main process lives; exiting host");
            UiDispatcherQueue?.TryEnqueue(ExitStudioHost);
        };

        try
        {
            await RunStudioHostBootAsync(client);
        }
        catch (Exception ex)
        {
            Log(
                $"[StudioHost] Boot failed: {ex.GetType().FullName} " +
                $"(0x{ex.HResult:X8}): {ex.Message}{Environment.NewLine}{ex}");
            ExitStudioHost();
        }
    }

    private async Task RunStudioHostBootAsync(SettingsIpcClient client)
    {
        Log("[StudioHost] IPC connected");
        var settings = new SettingsService(ReadOnlySettingsPersistenceSink.Instance);

        (bool bootstrapOk, string? bootstrapJson, string? bootstrapError) =
            await client.RequestAsync(StudioIpcMethods.Bootstrap);
        if (!bootstrapOk || string.IsNullOrWhiteSpace(bootstrapJson))
        {
            throw new InvalidOperationException(
                $"Studio bootstrap unavailable: {bootstrapError ?? "empty response"}");
        }

        StudioBootstrapResponse? bootstrap = SettingsIpcPayload.Decode(
            bootstrapJson,
            SettingsIpcJsonContext.Default.StudioBootstrapResponse);
        if (bootstrap is null || string.IsNullOrWhiteSpace(bootstrap.SettingsSnapshotJson))
        {
            throw new InvalidOperationException("Studio bootstrap payload was malformed.");
        }

        settings.AdoptExternalSnapshot(bootstrap.SettingsSnapshotJson);
        if (bootstrap.LastMcpClient is { } initialMcpClient)
        {
            DeskBoxAgentBridgeServer.PublishExternalMcpClientConnected(initialMcpClient);
        }

        var localization = new LocalizationService(settings);
        var theme = new ThemeService(settings);

        SettingsService = settings;
        LocalizationService = localization;
        ThemeService = theme;
        _studioHostSettingsService = settings;
        _studioHostLocalizationService = localization;
        _studioHostThemeService = theme;

        _ = Task.Run(() => ProcessEnvironmentSyncService.RefreshProcessEnvironmentFromRegistry("studio-startup"));

        var remoteBridge = new RemoteStudioHostBridge(
            client,
            settings,
            theme,
            Log,
            LogVerbose,
            mainProcessId: (uint)_studioHostMainProcessId);
        _studioHostBridge = remoteBridge;

        CustomWidgetDataStore.SaveRelay = async (widgetId, storeJson, clearAttachments) =>
        {
            (bool ok, _, string? storeError) = await client.RequestAsync(
                StudioIpcMethods.ApplyStoreData,
                CustomWidgetDataStore.ComposeRelayPayload(widgetId, storeJson, clearAttachments));
            if (!ok)
            {
                throw new InvalidOperationException(
                    $"Studio custom widget store apply rejected for {widgetId}: {storeError ?? "unknown"}");
            }
        };

        client.EventReceived += OnStudioHostEventReceived;
        ArmStudioHostWatchdog(_studioHostMainProcessId);

        var window = new CustomWidgetStudioWindow(localization, remoteBridge);
        _studioHostWindow = window;
        theme.TrackWindow(window);
        window.Session.AgentUpsertTargetPotentiallyChanged += ReportStudioHostWindowState;
        window.Activated += (_, eventArgs) =>
        {
            if (eventArgs.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated)
            {
                CustomWidgetAiCredentialStore.Shared.CheckForExternalChangesAndNotify();
                CustomWidgetStudioPreferencesStore.Shared.CheckForExternalChangesAndNotify();
            }
        };
        window.Closed += (_, _) =>
        {
            Log("[StudioHost] Window closed; exiting process immediately");
            ExitStudioHost();
        };

        if (bootstrap.InitialPreviewIntent is { } previewIntent &&
            !string.IsNullOrWhiteSpace(previewIntent.ManifestJson))
        {
            window.Initialize(null, null);
            window.OpenExternalManifestPreview(
                previewIntent.ManifestJson,
                previewIntent.SourceLabel,
                previewIntent.LogicJs,
                StudioLocaleWireHelper.Deserialize(previewIntent.LocalesJson, previewIntent.HasLocales),
                previewIntent.OriginKind);
        }
        else
        {
            StudioShowIntentPayload showIntent = bootstrap.InitialShowIntent
                ?? new StudioShowIntentPayload(null, null, AutoStartGeneration: false);
            ApplyStudioShowIntentToWindow(window, showIntent, isInitialOpen: true);
        }

        window.ShowWindow();
        ReportStudioHostWindowState();
        _ = client.RequestAsync(StudioIpcMethods.HostBootReady);
        Log("[StudioHost] Window shown");
    }

    private static void ApplyStudioShowIntentToWindow(
        CustomWidgetStudioWindow window,
        StudioShowIntentPayload intent,
        bool isInitialOpen)
    {
        bool isForkRequest = !string.IsNullOrWhiteSpace(intent.WidgetId) &&
                             intent.WidgetId.Trim().StartsWith("fork:", StringComparison.OrdinalIgnoreCase);
        string? forkSourceId = isForkRequest
            ? intent.WidgetId!.Trim()["fork:".Length..].Trim()
            : null;

        if (isForkRequest && !string.IsNullOrWhiteSpace(forkSourceId))
        {
            window.InitializeForkFromWidget(forkSourceId, intent.InitialIdea, intent.AutoStartGeneration);
        }
        else if (isInitialOpen)
        {
            window.Initialize(intent.WidgetId, intent.InitialIdea, intent.AutoStartGeneration);
        }
        else
        {
            window.RequestTarget(intent.WidgetId, intent.InitialIdea, intent.AutoStartGeneration);
        }
    }

    private void ReportStudioHostWindowState()
    {
        if (Volatile.Read(ref _studioHostExitStarted) != 0 ||
            _studioHostClient is not { } client ||
            _studioHostWindow is not { } window)
        {
            return;
        }

        long stateSeq = Interlocked.Increment(ref _studioHostWindowStateSequence);
        string payload = SettingsIpcPayload.Encode(
            new StudioWindowStatePayload(
                WindowHandle: window.WindowHandle.ToInt64(),
                TargetWidgetId: window.TryGetTargetWidgetIdForAgentUpsert(),
                StateSequence: stateSeq),
            SettingsIpcJsonContext.Default.StudioWindowStatePayload);
        _ = Task.Run(async () =>
        {
            try
            {
                await client.RequestAsync(StudioIpcMethods.ReportWindowState, payload).ConfigureAwait(false);
            }
            catch
            {
            }
        });
    }

    private void OnStudioHostEventReceived(string method, string? payload)
    {
        void RunOnUi(Action action) => UiDispatcherQueue?.TryEnqueue(() => action());

        switch (method)
        {
            case StudioIpcMethods.HostShow:
            {
                StudioShowIntentPayload? intent = !string.IsNullOrWhiteSpace(payload)
                    ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.StudioShowIntentPayload)
                    : new StudioShowIntentPayload(null, null, AutoStartGeneration: false);
                if (intent is not null)
                {
                    RunOnUi(() =>
                    {
                        if (_studioHostWindow is { } window)
                        {
                            ApplyStudioShowIntentToWindow(window, intent, isInitialOpen: false);
                            window.ShowWindow();
                            ReportStudioHostWindowState();
                        }
                    });
                }
                break;
            }

            case StudioIpcMethods.HostShowPreview:
            {
                StudioShowPreviewIntentPayload? preview = !string.IsNullOrWhiteSpace(payload)
                    ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.StudioShowPreviewIntentPayload)
                    : null;
                if (preview is not null && !string.IsNullOrWhiteSpace(preview.ManifestJson))
                {
                    RunOnUi(() =>
                    {
                        if (_studioHostWindow is { } window)
                        {
                            window.OpenExternalManifestPreview(
                                preview.ManifestJson,
                                preview.SourceLabel,
                                preview.LogicJs,
                                StudioLocaleWireHelper.Deserialize(preview.LocalesJson, preview.HasLocales),
                                preview.OriginKind);
                            window.ShowWindow();
                            ReportStudioHostWindowState();
                        }
                    });
                }
                break;
            }

            case StudioIpcMethods.LayoutChanged:
                RunOnUi(() =>
                {
                    if (_studioHostSettingsService?.ApplyExternalLayout(payload ?? string.Empty) == true &&
                        _studioHostWindow is { IsClosed: false } window)
                    {
                        window.SyncHeaderCommitActionsForDesktopState();
                    }
                });
                break;

            case StudioIpcMethods.SettingsReloaded:
                RunOnUi(async () =>
                {
                    if (_studioHostSettingsService is not { } settingsService)
                    {
                        return;
                    }

                    string beforeLanguage = settingsService.Settings.Core.Language;
                    if (await settingsService.TryReloadFromDiskAsync())
                    {
                        _studioHostThemeService?.RefreshAppearance();
                        if (!string.Equals(beforeLanguage, settingsService.Settings.Core.Language, StringComparison.Ordinal))
                        {
                            _studioHostLocalizationService?.NotifyExternalLanguageChange();
                        }
                    }
                });
                break;

            case StudioIpcMethods.DisplayTopologyChanged:
                RunOnUi(() =>
                {
                    RaiseDisplayTopologyChanged();
                    _studioHostBridge?.NotifyDisplayTopologyChanged();
                });
                break;

            case StudioIpcMethods.AgentWidgetUpserted:
            {
                StudioAgentUpsertEventPayload? upserted = !string.IsNullOrWhiteSpace(payload)
                    ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.StudioAgentUpsertEventPayload)
                    : null;
                if (upserted is not null)
                {
                    RunOnUi(() =>
                    {
                        if (_studioHostWindow is { } window)
                        {
                            window.OnExternalAgentWidgetUpserted(
                                upserted.WidgetId,
                                upserted.WidgetName,
                                upserted.ManifestJson,
                                upserted.LogicJs,
                                upserted.HasLogicJs,
                                StudioLocaleWireHelper.Deserialize(upserted.LocalesJson, upserted.HasLocales),
                                upserted.HasLocales);
                            ReportStudioHostWindowState();
                        }
                    });
                }
                break;
            }

            case StudioIpcMethods.McpClientConnected:
            {
                ConnectedMcpClientInfo? info = !string.IsNullOrWhiteSpace(payload)
                    ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.ConnectedMcpClientInfo)
                    : null;
                if (info is not null)
                {
                    DeskBoxAgentBridgeServer.PublishExternalMcpClientConnected(info);
                }
                break;
            }

            case StudioIpcMethods.McpActivityRecorded:
            {
                StudioMcpActivityPayload? activity = !string.IsNullOrWhiteSpace(payload)
                    ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.StudioMcpActivityPayload)
                    : null;
                if (activity is not null)
                {
                    DeskBoxAgentBridgeServer.PublishExternalMcpActivity(activity.Op, activity.Detail);
                }
                break;
            }

            case StudioIpcMethods.AgentStageChanged:
                CustomWidgetAgentStageStore.NotifyExternalChanged();
                break;

            case StudioIpcMethods.RequestStudioContext:
            {
                StudioContextRequestPayload? req = !string.IsNullOrWhiteSpace(payload)
                    ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.StudioContextRequestPayload)
                    : null;
                if (req is not null && !string.IsNullOrWhiteSpace(req.RequestId))
                {
                    RunOnUi(() =>
                    {
                        if (_studioHostClient is not { } client)
                        {
                            return;
                        }

                        string contextJson;
                        string? targetId = null;
                        try
                        {
                            if (_studioHostWindow is { } window)
                            {
                                contextJson = DeskBoxAgentBridgeServer.BuildSuccessJson(
                                    writer => window.WriteStudioContextTo(writer, req.IncludeManifest));
                                targetId = window.TryGetTargetWidgetIdForAgentUpsert();
                            }
                            else
                            {
                                contextJson = DeskBoxAgentBridgeServer.BuildHeadlessStudioContextResponseJson(
                                    _studioHostLocalizationService?.CurrentCultureName);
                            }
                        }
                        catch (Exception ex)
                        {
                            contextJson = DeskBoxAgentBridgeServer.BuildErrorJson(ex.Message);
                        }

                        string reportPayload = SettingsIpcPayload.Encode(
                            new StudioContextReportPayload(req.RequestId, contextJson, targetId),
                            SettingsIpcJsonContext.Default.StudioContextReportPayload);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await client.RequestAsync(StudioIpcMethods.ReportStudioContext, reportPayload)
                                    .ConfigureAwait(false);
                            }
                            catch
                            {
                            }
                        });
                    });
                }
                break;
            }

            case StudioIpcMethods.CaptureStudioStagePng:
            {
                if (StudioStagePngWireHelper.TryDecodeRequest(payload, out StudioStagePngCaptureRequest? pngReq) &&
                    pngReq is not null)
                {
                    RunOnUi(async () =>
                    {
                        if (_studioHostClient is not { } client)
                        {
                            return;
                        }

                        StudioStagePngCaptureReport report;
                        try
                        {
                            if (_studioHostWindow is { } window &&
                                window.Session.StagePreviewCapturer is { } capturer)
                            {
                                (byte[] PngBytes, int Width, int Height, int OverflowPx)? captured =
                                    await capturer(pngReq.CapsuleMode);
                                report = captured is { PngBytes.Length: > 0 } valid
                                    ? new StudioStagePngCaptureReport(
                                        pngReq.RequestId,
                                        Ok: true,
                                        PngBytes: valid.PngBytes,
                                        Width: valid.Width,
                                        Height: valid.Height,
                                        OverflowPx: valid.OverflowPx)
                                    : new StudioStagePngCaptureReport(
                                        pngReq.RequestId,
                                        Ok: false,
                                        PngBytes: null,
                                        Width: 0,
                                        Height: 0,
                                        OverflowPx: 0,
                                        Error: "Studio stage preview is not available.");
                            }
                            else
                            {
                                report = new StudioStagePngCaptureReport(
                                    pngReq.RequestId,
                                    Ok: false,
                                    PngBytes: null,
                                    Width: 0,
                                    Height: 0,
                                    OverflowPx: 0,
                                    Error: "Studio stage preview capturer is not registered.");
                            }
                        }
                        catch (Exception ex)
                        {
                            report = new StudioStagePngCaptureReport(
                                pngReq.RequestId,
                                Ok: false,
                                PngBytes: null,
                                Width: 0,
                                Height: 0,
                                OverflowPx: 0,
                                Error: ex.Message);
                        }

                        string reportPayload = StudioStagePngWireHelper.EncodeReport(report);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await client.RequestAsync(StudioIpcMethods.ReportStagePng, reportPayload)
                                    .ConfigureAwait(false);
                            }
                            catch
                            {
                            }
                        });
                    });
                }
                break;
            }

            case StudioIpcMethods.HostShutdown:
                RunOnUi(ExitStudioHost);
                break;
        }
    }

    private void ArmStudioHostWatchdog(int mainProcessId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var main = Process.GetProcessById(mainProcessId);
                _ = main.StartTime;
                await main.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            Log($"[StudioHost] Main process {mainProcessId} exited; shutting down host");
            UiDispatcherQueue?.TryEnqueue(ExitStudioHost);
            if (UiDispatcherQueue is null)
            {
                ExitStudioHost();
            }
        });
    }

    internal void ExitStudioHost()
    {
        if (Interlocked.Exchange(ref _studioHostExitStarted, 1) != 0)
        {
            return;
        }

        DeskBox.Platform.UiDispatch.MarkShutdown();

        // Backstop guarantees immediate termination even if a COM/WinRT finalizer blocks Environment.Exit.
        var hardKill = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(2));
            try
            {
                DrainLogQueue();
                Process.GetCurrentProcess().Kill();
            }
            catch
            {
            }
        })
        {
            IsBackground = true,
            Name = "DeskBox.StudioHostExitBackstop",
        };
        hardKill.Start();

        try
        {
            _studioHostWindow?.PersistDraftOnExit();
        }
        catch (Exception ex)
        {
            Log($"[StudioHost] PersistDraftOnExit failed: {ex.Message}");
        }

        if (_studioHostClient is { } client)
        {
            try
            {
                string pidText = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
                _ = Task.Run(() => client.RequestAsync(StudioIpcMethods.HostFarewell, pidText))
                    .Wait(TimeSpan.FromMilliseconds(400));
            }
            catch (Exception ex)
            {
                Log($"[StudioHost] Farewell send failed: {ex.Message}");
            }

            try
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log($"[StudioHost] IPC dispose failed: {ex.Message}");
            }
        }

        DrainLogQueue();
        Environment.Exit(0);
    }
}
