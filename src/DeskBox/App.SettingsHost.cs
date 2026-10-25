using System.Diagnostics;

namespace DeskBox;

using DeskBox.Contracts;
using DeskBox.Features.QuickCapture;
using DeskBox.Models;
using DeskBox.Services;

/// <summary>
/// The real settings host (batch 3c-3b): a second DeskBox.exe instance
/// launched as <c>DeskBox.exe --settings &lt;mainPid&gt;</c> shows the actual
/// settings window in its own process, so closing the window exits the
/// process and releases everything the old hide-and-reuse window kept
/// resident. Everything host-side runs over the settings IPC pipe: settings
/// commits travel through the remote persistence sink, host commands through
/// the remote facades, and the main process pushes reloads, hotkey
/// registration changes, and backup/update events back.
/// </summary>
public partial class App
{
    /// <summary>
    /// Command-line contract of the settings host. Exact token match only —
    /// a Contains check would also swallow the jump list's --open-settings.
    /// </summary>
    internal const string SettingsHostLaunchArgument = "--settings";

    internal sealed class SettingsHostLaunch
    {
        internal const string Argument = SettingsHostLaunchArgument;

        internal static bool IsSettingsHostLaunch(string[] arguments)
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

        /// <summary>
        /// Parses "--settings &lt;pid&gt;"; returns 0 when the pid is missing or
        /// malformed (the host exits with a log line in that case).
        /// </summary>
        internal static int TryGetMainProcessId(string[] arguments)
        {
            for (int i = 1; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], Argument, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(arguments[i + 1], out int pid) &&
                    pid > 0)
                {
                    return pid;
                }
            }

            return 0;
        }

        /// <summary>The value after "--settings-section", or null (spawn deep link).</summary>
        internal static string? TryGetSectionArgument(string[] arguments) =>
            TryGetValueAfter(arguments, "--settings-section");

        /// <summary>The value after "--settings-glance", or null (spawn deep link).</summary>
        internal static string? TryGetGlanceArgument(string[] arguments) =>
            TryGetValueAfter(arguments, "--settings-glance");

        /// <summary>Whether the one-click-update flag rides the launch (spawn intent).</summary>
        internal static bool HasOneClickUpdateArgument(string[] arguments)
        {
            for (int i = 1; i < arguments.Length; i++)
            {
                if (string.Equals(arguments[i], OneClickUpdateArgument, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The value after the install-result argument, normalized like the
        /// main process's own --update-install-result parser (spawn intent).
        /// </summary>
        internal static string? TryGetInstallResultArgument(string[] arguments)
        {
            string? raw = TryGetValueAfter(arguments, InstallResultArgument);
            return string.IsNullOrWhiteSpace(raw) ? null : NormalizeUpdateInstallOutcome(raw);
        }

        internal const string SectionArgument = "--settings-section";
        internal const string GlanceArgument = "--settings-glance";
        internal const string OneClickUpdateArgument = "--settings-one-click-update";
        internal const string InstallResultArgument = "--settings-install-result";

        private static string? TryGetValueAfter(string[] arguments, string name)
        {
            for (int i = 1; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[i + 1];
                }
            }

            return null;
        }
    }

    private bool _isSettingsHost;
    private SettingsIpcClient? _settingsHostClient;
    private int _settingsHostMainProcessId;
    private bool _settingsHostExitStarted;
    private Views.SettingsWindow? _settingsHostWindow;
    private RemoteSearchConnectionClient? _settingsHostEverythingConnection;

    // Spawn-to-shown trace for the measurement script and crash diagnosis,
    // in the temp directory so it never touches the data root.
    private static readonly long SettingsHostBaselineTimestamp = Stopwatch.GetTimestamp();

    private static void SettingsHostTrace(string marker)
    {
        double elapsedMs =
            (Stopwatch.GetTimestamp() - SettingsHostBaselineTimestamp) * 1000.0 /
            Stopwatch.Frequency;
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deskbox-settings-host.log"),
                $"{DateTimeOffset.UtcNow:O} +{elapsedMs:F0}ms pid={Environment.ProcessId} {marker}{Environment.NewLine}");
        }
        catch (System.IO.IOException)
        {
            // Trace logging must never take the host down.
        }
    }

    internal bool IsSettingsHost => _isSettingsHost;

    private SettingsService? SettingsHostSettingsService { get; set; }

    private LocalizationService? SettingsHostLocalizationService { get; set; }

    /// <summary>
    /// App-constructor branch for the settings host: initialize XAML only.
    /// The LogPath static initializer has already switched to the host log
    /// file, and every primary-instance responsibility below this branch
    /// (AUMID, activation event, single-instance mutex, watchdog, elevation
    /// gate, DI container) stays skipped.
    /// </summary>
    private void InitializeSettingsHost()
    {
        _isSettingsHost = true;
        // Group the host window with the main app's taskbar button (without
        // touching the Start Menu shortcut — see RegisterProcessAppUserModelIdOnly).
        JumpListService.RegisterProcessAppUserModelIdOnly();
        SettingsHostTrace("branch-entered");
        Log("[SettingsHost] Constructor branch entered");
        InitializeComponent();
        UnhandledException += (_, eventArgs) =>
        {
            Log($"[SettingsHost] Unhandled exception: {eventArgs.Message}");
            if (eventArgs.Exception is { } unhandled)
            {
                Log($"[SettingsHost] Unhandled exception detail: {unhandled}");
            }

            eventArgs.Handled = true;
        };
    }

    /// <summary>
    /// OnLaunched branch of the settings host: connect to the main process,
    /// build the host-side service subset, show the real settings window,
    /// and arm the main-process watchdog. Runs on the UI thread.
    /// </summary>
    private async void LaunchSettingsHostAsync()
    {
        UiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _settingsHostMainProcessId = SettingsHostLaunch.TryGetMainProcessId(
            Environment.GetCommandLineArgs());
        if (_settingsHostMainProcessId == 0)
        {
            Log("[SettingsHost] Missing main process id argument; exiting");
            ExitSettingsHost();
            return;
        }

        SettingsIpcClient client;
        try
        {
            client = await SettingsIpcClient.ConnectAsync(
                DeskBoxDataPathService.Current.SettingsIpcPipeName,
                TimeSpan.FromMilliseconds(500),
                attempts: 20);
        }
        catch (Exception ex)
        {
            Log($"[SettingsHost] IPC connect failed: {ex.Message}");
            ExitSettingsHost();
            return;
        }

        _settingsHostClient = client;
        // Batch R12 (sequence audit, defect E): a broken pipe with the
        // main process still alive used to leave this window running with
        // every subsequent command failing into the silent-loss chain.
        // The host is useless without the pipe — say so and exit in order.
        client.Disconnected += () =>
        {
            Log("[SettingsHost] Pipe disconnected while the main process lives; exiting host");
            UiDispatcherQueue?.TryEnqueue(ExitSettingsHost);
        };
        try
        {
            await RunSettingsHostBootAsync(client);
        }
        catch (Exception ex)
        {
            // A busy main process can make any boot step (load, VM
            // construction) throw — without this the host stayed alive with
            // no window (zombie) until the main process died (batch R5).
            Log($"[SettingsHost] Boot failed: {ex.Message}");
            ExitSettingsHost();
        }
    }

    private async Task RunSettingsHostBootAsync(SettingsIpcClient client)
    {
        Log("[SettingsHost] IPC connected");
        var settings = new SettingsService(new RemoteSettingsPersistenceSink(client));
        Log("[SettingsHost] SettingsService constructed");

        // Boot adoption (batch R8): the host mirrors the MAIN process's
        // in-memory graph — always at least as fresh as disk — instead of
        // disk-loading. A corrupt settings.json + .bak pair must not seed a
        // defaults graph that a later host commit would push back over the
        // main process's healthy state, and the load-time store writes
        // (layout seed adoption, .bak promotion, legacy migration) belong
        // to the single writer. The payload carries the live widget-layout
        // slice inside the AppSettings graph.
        (bool snapshotOk, string? snapshotJson, string? snapshotError) =
            await client.RequestAsync(SettingsIpcMethods.SettingsSnapshot);
        if (!snapshotOk || string.IsNullOrEmpty(snapshotJson))
        {
            throw new InvalidOperationException(
                $"Settings snapshot unavailable: {snapshotError ?? "empty response"}");
        }

        settings.AdoptExternalSnapshot(snapshotJson);
        Log("[SettingsHost] Settings adopted from main-process snapshot");

        // Commit failures must reach the user, not just the host log: the
        // sink's refusal now raises PersistenceFailed (deep review R13, R12
        // debt ①), and the window banner keeps the user from editing a
        // surface that silently would not persist. The banner closes again
        // on the next successful commit — the recovery signal.
        settings.PersistenceFailed += failure =>
        {
            Log($"[SettingsHost] Settings commit failed ({failure.Operation}): {failure.Message}");
            UiDispatcherQueue?.TryEnqueue(() => _settingsHostWindow?.ShowSaveFailedBanner());
        };
        settings.LocalPersistCompleted += _ =>
        {
            UiDispatcherQueue?.TryEnqueue(() => _settingsHostWindow?.DismissSaveFailedBanner());
        };

        // The autostart toggle executes in THIS process (the settings page
        // calls StartupService directly). Without Configure the static fell
        // back to the parameterless DirectStartupService — the wrong backend
        // on the Store channel (Run key pointing into WindowsApps instead
        // of the StartupTask API) and a null mode writer on Direct, so
        // autostart mode switches never persisted (batch R9).
        StartupService.Configure(StartupServiceFactory.Create(DistributionService, settings));

        // Per-widget Glance data edits from the settings section ride the
        // IPC relay: the desktop widgets' live Changed listeners and the
        // glance store file both belong to the MAIN process. Without the
        // relay the host's direct file writes were invisible to open
        // widgets and could be silently reverted by the main store's
        // cached write-back (batch R8).
        GlanceWidgetStore.SaveRelay = async (widgetId, dataJson) =>
        {
            // Failures THROW on purpose: the store rolls its cache back and
            // the section's SaveAsync surfaces the error path — a log-only
            // relay used to leave the host showing a fabricated success
            // (batch R9).
            (bool ok, _, string? glanceError) = await client.RequestAsync(
                SettingsIpcMethods.WidgetGlanceApplyData,
                widgetId + "\n" + dataJson);
            if (!ok)
            {
                // Deep review R13-2 (D14 audit): glance saves do not ride
                // the settings sink, so the save-failed banner never heard
                // about them — surface the failure here too (the throw
                // alone only reached the host log through the section's
                // async-void handlers).
                UiDispatcherQueue?.TryEnqueue(() => _settingsHostWindow?.ShowSaveFailedBanner());
                throw new InvalidOperationException(
                    $"Glance apply rejected for {widgetId}: {glanceError ?? "unknown"}");
            }
        };

        var localization = new LocalizationService(settings);
        var theme = new ThemeService(settings);

        // The settings tree reads these two through App.Current; publish the
        // host instances for the lifetime of the process.
        SettingsService = settings;
        SettingsHostSettingsService = settings;
        SettingsHostLocalizationService = localization;
        LocalizationService = localization;

        client.EventReceived += OnSettingsHostCommandEvent;
        client.EventReceived += (method, payload) =>
        {
            if (method != SettingsIpcMethods.LayoutChanged)
            {
                return;
            }

            UiDispatcherQueue.TryEnqueue(() => settings.ApplyExternalLayout(payload ?? string.Empty));
        };

        // The reload/echo bridge: the main process pushes settings-reloaded
        // after every durable save; pushes carrying our own commit id are
        // the round-trip of our write and must not be reloaded.
        client.EventReceived += (method, payload) =>
        {
            if (method != SettingsIpcMethods.SettingsReloaded)
            {
                return;
            }

            if (SettingsIpcReloadPayload.TryParse(payload, out _, out long? hostCommitId) &&
                hostCommitId is { } hostId &&
                settings.IsLatestOutboundCommit(hostId))
            {
                return;
            }

            UiDispatcherQueue.TryEnqueue(async () =>
            {
                string beforeTheme = settings.Settings.Theme;
                string beforeAccent = settings.Settings.AccentColorMode;
                string beforeCustomAccent = settings.Settings.CustomAccentColor;
                string beforeLanguage = settings.Settings.Language;
                // Skip the reload while a debounced save is pending: the
                // in-memory graph holds newer user edits, and a whole-graph
                // reload would roll them back AND let the pending save
                // persist the rollback (batch R7). Convergence comes from
                // the pending commit itself (or the next push).
                if (!settings.HasPendingSave &&
                    await settings.TryReloadFromDiskAsync())
                {
                    if (!string.Equals(beforeTheme, settings.Settings.Theme, StringComparison.Ordinal) ||
                        !string.Equals(beforeAccent, settings.Settings.AccentColorMode, StringComparison.Ordinal) ||
                        !string.Equals(beforeCustomAccent, settings.Settings.CustomAccentColor, StringComparison.Ordinal))
                    {
                        theme.RefreshAppearance();
                    }

                    if (!string.Equals(beforeLanguage, settings.Settings.Language, StringComparison.Ordinal))
                    {
                        localization.NotifyExternalLanguageChange();
                    }
                }
            });
        };

        // Appearance sliders stream previews through the host-side preview
        // channel (66ms debounce): forward each preview tick over the pipe;
        // the main process applies it preview-only (no durable write). The
        // snapshot commit still happens on release, exactly like the
        // pre-split single-process flow.
        settings.AppearancePreviewChanged += () =>
        {
            // Batch R11: serialize under the settings lock — this handler
            // runs on the 66ms debounce pool thread, exactly when the UI
            // thread is still mutating the live graph. The persistence
            // sink got the same fix in R9; this forwarder kept the
            // lock-free Serialize call and could ship a torn preview (or
            // throw on a mutated collection, silently dropping the frame).
            string previewJson = settings.SerializeCurrentSettings();
            _ = client.RequestAsync(
                DeskBox.Services.SettingsIpcMethods.AppearancePreview,
                previewJson);
        };

        ArmSettingsHostWatchdog(_settingsHostMainProcessId);
        Log("[SettingsHost] Watchdog armed");

        var input = BuildSettingsHostFactoryInput(settings, localization, theme, client);
        Log("[SettingsHost] Factory input built");
        var window = input.CreateWindow();
        _settingsHostWindow = window;
        Log("[SettingsHost] Window constructed");
        window._isHostProcessWindow = true;
        bool activatedTraced = false;
        window.Activated += (_, _) =>
        {
            if (!activatedTraced)
            {
                activatedTraced = true;
                SettingsHostTrace("window-activated");
            }
        };
        window.Closed += (_, _) =>
        {
            SettingsHostTrace("window-closed");
            ExitSettingsHost();
        };
        window.Activate();

        // Spawn-carried intent (batch R1): section/glance deep links, the
        // toast one-click-update flow, and the installer result dialog.
        // These ride the command line because the host cannot receive
        // pushed events before its pipe connects — pushing right after a
        // spawn is a silent drop.
        string[] launchArguments = Environment.GetCommandLineArgs();
        if (SettingsHostLaunch.TryGetGlanceArgument(launchArguments) is { } launchGlance)
        {
            window.ShowGlanceSection(launchGlance);
        }
        else if (SettingsHostLaunch.TryGetSectionArgument(launchArguments) is { } launchSection)
        {
            window.ShowSection(launchSection);
            if (string.Equals(launchSection, "About", StringComparison.Ordinal))
            {
                window.BringUpdateStatusIntoView();
            }
        }

        if (SettingsHostLaunch.HasOneClickUpdateArgument(launchArguments))
        {
            _ = window.RunOneClickUpdateFlowAsync();
        }

        if (SettingsHostLaunch.TryGetInstallResultArgument(launchArguments) is { } launchInstallResult)
        {
            window.QueueUpdateInstallResultDialog(launchInstallResult);
        }

        // Warm the Everything connection cache off the startup path: a busy
        // main process must not add a synchronous round trip before the
        // first frame (batch R3).
        if (_settingsHostEverythingConnection is { } everythingWarmup)
        {
            _ = Task.Run(everythingWarmup.Prime);
        }

        // Deep review R13 (D5): a host from a PREVIOUS era that crashed
        // mid-hotkey-recording never sent the resume, and the coordinator's
        // exit guard now skips stale eras (generation check) — clear any
        // leftover suspension here. Resume is idempotent; the theoretical
        // overlap with a closing previous host's own recorder is narrower
        // than the stuck-suspension bug this closes.
        _ = client.RequestAsync(SettingsIpcMethods.HotkeyResume);

        // Boot-ready notice (deep review R13, D5): the coordinator queued
        // any intent pushes that arrived before the subscriptions above and
        // this window existed; tell it to flush them now, when the pipe,
        // the event handlers, and the window are all guaranteed live.
        _ = client.RequestAsync(SettingsIpcMethods.HostBootReady);

        SettingsHostTrace("onlaunched-done");
        Log("[SettingsHost] Window shown");
    }

    private SettingsWindowFactoryInput BuildSettingsHostFactoryInput(
        SettingsService settings,
        LocalizationService localization,
        ThemeService theme,
        SettingsIpcClient client)
    {
        var widgetActions = new RemoteSettingsWidgetActions(
            client,
            settings,
            // Deep review R13-2 (D15): layout write failures surface on the
            // save-failed banner instead of only the log.
            onWriteFailure: () => UiDispatcherQueue?.TryEnqueue(
                () => _settingsHostWindow?.ShowSaveFailedBanner()));
        var appActions = new RemoteSettingsAppActions(
            client,
            (uint)_settingsHostMainProcessId,
            // Batch R11: push events (topology/screen-home) must hop to the
            // UI thread — see the facade's RaiseOnUi.
            action => UiDispatcherQueue is { } queue && queue.TryEnqueue(() => action()));
        // Forward the display-topology push onto the App-level event: the
        // host's identify-display overlay self-closes on it, and nothing
        // raised it in the host process — the guard was dead code here
        // (batch R9).
        appActions.DisplayTopologyChanged += () => UiDispatcherQueue.TryEnqueue(
            () => DisplayTopologyChanged?.Invoke());

        // The Todo/QuickCapture enable switches are feature-widget state: the
        // enabled dictionary lives in the widget-layout domain, which the main
        // process strips from every snapshot commit (main-owned). A host-side
        // commit alone therefore never flips the widget — the switch MUST ride
        // the SetFeatureWidgetEnabled command so the MAIN process runs the
        // authoritative chain (dictionary write, widget apply, reminder
        // reconcile). The comment that used to sit here claimed SettingsChanged
        // re-application covers the side effects; that only holds for
        // settings.json slices (Search), never for the dictionary kinds
        // (Todo/QuickCapture) — batch R8, same family as the R7 Search fix.
        var todo = new TodoSettingsCoordinator(
            settings,
            setWidgetEnabled: async enabled =>
            {
                // Optimistic mirror so the switch survives the post-roundtrip
                // Refresh(); the command response and the layout-changed push
                // deliver the authoritative dictionary.
                FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.Todo, enabled);
                bool applied = await widgetActions.SetFeatureWidgetEnabled(WidgetKind.Todo, enabled);
                if (applied != enabled)
                {
                    FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.Todo, applied);
                }
            });
        var featureWidgets = new FeatureWidgetsSettingsCoordinator(settings);
        var everythingConnection = new RemoteSearchConnectionClient(client);
        _settingsHostEverythingConnection = everythingConnection;
        // The search section's hotkey card rides the search-hotkey IPC
        // commands (batch R9): the live registration state lives only in
        // the MAIN process, and a null getHotkey here used to render the
        // card permanently unavailable — toggle/capture/reset could never
        // reach the service. CurrentGesture reads the local settings
        // mirror exactly like the real service does.
        var hotkey = new RemoteSettingsHotkeyActions(client);
        var searchHotkey = new RemoteSearchHotkeyController(
            hotkey,
            () => GlobalHotkeyService.NormalizeGesture(
                settings.Settings.Search.SearchHotkeyModifiers,
                settings.Settings.Search.SearchHotkeyKey),
            // Deep review R13-3: the "owned by main hotkey" predicate is
            // computed locally from the mirror, with the same normalization
            // GlobalHotkeyService.CurrentActivation applies — no round trip.
            () => GlobalHotkeyService.NormalizeActivation(
                settings.Settings.GlobalHotkeyActivationKind,
                settings.Settings.GlobalHotkeyModifiers,
                settings.Settings.GlobalHotkeyKey));
        var search = new SearchSettingsCoordinator(
            settings,
            localization,
            getConnection: () => everythingConnection,
            ensureConnection: () => everythingConnection,
            getHotkey: () => searchHotkey,
            setContentSearchEnabled: _ => { },
            // Explicit no-op (null falls back to a full settings save — a
            // write amplifier on every external enable change, batch R7).
            applyWidget: (_, _) => Task.CompletedTask,
            setRuntimeEnabled: null,
            tryEnqueue: action => UiDispatcherQueue.TryEnqueue(() => action()),
            reportError: ex => Log($"[SearchSettings] Operation failed: {ex}"));
        var quickCaptureClipboard = new QuickCaptureClipboardRuntime(
            shouldListen: () => false,
            create: () => throw new InvalidOperationException(
                "The settings host never listens to the clipboard"),
            reportError: ex => Log($"[QuickCapture] Clipboard runtime failed: {ex}"));
        var quickCapture = new QuickCaptureSettingsCoordinator(
            settings,
            quickCaptureClipboard,
            // The enable chain rides the feature-switch command (see the Todo
            // wiring above): the main process owns the dictionary and the
            // widget window, and decides reveal like every other
            // SetFeatureWidgetEnabled caller. Optimistic mirror + applied
            // rollback, exactly like the Todo wiring — the R8/R9 batch
            // comments claimed "the same treatment", but this lambda used
            // to discard the applied value, so a refused switch left the UI
            // on a fabricated success until the next layout push (deep
            // review R13-2, D13/D16 audit).
            applyWidget: async (enabled, _) =>
            {
                FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, enabled);
                bool applied = await widgetActions.SetFeatureWidgetEnabled(WidgetKind.QuickCapture, enabled);
                if (applied != enabled)
                {
                    FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, applied);
                }
            },
            tryEnqueue: action => UiDispatcherQueue.TryEnqueue(() => action()),
            reportError: ex => Log($"[QuickCapture] Settings operation failed: {ex}"));

        return new SettingsWindowFactoryInput(
            settings, theme, localization,
            todo,
            featureWidgets,
            search,
            new RemoteBackupSettings(client),
            new RemoteBackupRestoreActions(client),
            quickCapture,
            new RemoteBackupCommands(client),
            new AppearanceSettingsCoordinator(
                settings,
                clearPerWidgetBackgroundsExternal: () => widgetActions.ClearPerWidgetBackgrounds()),
            new CapsuleSettingsCoordinator(settings),
            new InteractionSettingsCoordinator(settings),
            new FileDisplaySettingsCoordinator(settings),
            new FileStackSettingsCoordinator(settings),
            new GroupNavigationSettingsCoordinator(
                settings,
                applyExternal: change => widgetActions.ApplyGroupNavigationDefaults(change)),
            new ManagedStorageSettingsCoordinator(settings),
            new MaintenanceSettingsCoordinator(settings),
            new PerformanceSettingsCoordinator(settings),
            hotkey,
            widgetActions,
            new RemoteSettingsRestoreActions(client),
            appActions,
            new RemoteSettingsDataActions(client),
            new RemoteAppUpdateService(client),
            action => UiDispatcherQueue.TryEnqueue(() => action()));
    }

    /// <summary>
    /// Main-process commands to the host (batch 3d): navigation wakes,
    /// update/desktop-organization refreshes, and the shutdown notice.
    /// </summary>
    private void OnSettingsHostCommandEvent(string method, string? payload)
    {
        void RunOnUi(Action action) => UiDispatcherQueue.TryEnqueue(() => action());

        switch (method)
        {
            case SettingsIpcMethods.HostShow:
                RunOnUi(() =>
                {
                    if (_settingsHostWindow is { } window)
                    {
                        window.ShowWindow();
                        if (!string.IsNullOrEmpty(payload))
                        {
                            window.ShowSection(payload);
                            if (string.Equals(payload, "About", StringComparison.Ordinal))
                            {
                                window.BringUpdateStatusIntoView();
                            }
                        }
                    }
                });
                break;
            case SettingsIpcMethods.HostShowGlance:
                if (!string.IsNullOrEmpty(payload))
                {
                    string widgetId = payload;
                    RunOnUi(() => _settingsHostWindow?.ShowWindow());
                    RunOnUi(() => _settingsHostWindow?.ShowGlanceSection(widgetId));
                }

                break;
            case SettingsIpcMethods.HostUpdateStateRefresh:
                RunOnUi(() =>
                {
                    _settingsHostWindow?.ViewModel.RefreshCachedUpdateState();
                    _settingsHostWindow?.BringUpdateStatusIntoView();
                });
                break;
            case SettingsIpcMethods.HostQueueUpdateResult:
                if (!string.IsNullOrEmpty(payload))
                {
                    string outcome = payload;
                    RunOnUi(() => _settingsHostWindow?.QueueUpdateInstallResultDialog(outcome));
                }

                break;
            case SettingsIpcMethods.HostDesktopOrgRefresh:
                RunOnUi(() => _settingsHostWindow?.RefreshDesktopOrganizationState());
                break;
            case SettingsIpcMethods.HostOneClickUpdate:
                RunOnUi(async () =>
                {
                    if (_settingsHostWindow is { } window)
                    {
                        await window.RunOneClickUpdateFlowAsync();
                    }
                });
                break;
            case SettingsIpcMethods.HostShutdown:
                RunOnUi(ExitSettingsHost);
                break;
        }
    }

    /// <summary>
    /// Exits the host when the main process dies (crash, shutdown, sign-out):
    /// a settings window whose every command faults is worse than none. The
    /// StartTime snapshot guards against pid reuse.
    /// </summary>
    private void ArmSettingsHostWatchdog(int mainProcessId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var main = Process.GetProcessById(mainProcessId);
                _ = main.StartTime;
                await main.WaitForExitAsync();
            }
            catch (Exception)
            {
                // Process already gone: fall through and exit.
            }

            Log($"[SettingsHost] Main process {mainProcessId} exited; shutting down host");
            UiDispatcherQueue?.TryEnqueue(ExitSettingsHost);
            if (UiDispatcherQueue is null)
            {
                ExitSettingsHost();
            }
        });
    }

    /// <summary>
    /// The single exit path of the host: flush the host log, drop the pipe,
    /// and terminate. Hard-exit on purpose — mirroring the PoC finding, a
    /// bare Application.Exit defers through the XAML message loop and a
    /// half-dead settings window must not linger.
    /// </summary>
    internal void ExitSettingsHost()
    {
        // Re-entrancy guard (batch R12): the pipe Disconnected subscription
        // fires from the reader task — including during this very method's
        // own client dispose — and would schedule a second exit pass.
        if (_settingsHostExitStarted)
        {
            return;
        }

        _settingsHostExitStarted = true;
        // Farewell first (batch R11): the exit flush below keeps this
        // process alive for up to ~2s, and "process alive" is the main
        // process's spawn-vs-show criterion — without this notice, an
        // open-settings inside the window is pushed onto the dying host
        // and silently lost. Fire-and-forget: the flush that follows gives
        // the request its flight time, and a lost farewell merely falls
        // back to the old process-exit detection.
        try
        {
            _ = _settingsHostClient?.RequestAsync(
                SettingsIpcMethods.HostFarewell,
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Log($"[SettingsHost] Farewell send failed: {ex.Message}");
        }

        // Flush any debounced (1s) pending save first: the host process is
        // the writer, and dying inside the debounce window used to silently
        // drop the user's last edit (batch R5).
        // Batch R11: the flush must run on a pool thread. Its await chain
        // (SaveToFileOnlyAsync → sink) posts continuations through the
        // calling context — started on this UI thread they queued behind
        // the very Wait() below, so every close burned the full 2s budget
        // and logged a false "edit may be lost" (and an in-flight debounce
        // could genuinely outlive the wait).
        try
        {
            if (SettingsHostSettingsService is { } pendingService &&
                !Task.Run(() => pendingService.FlushPendingSaveAsync()).Wait(TimeSpan.FromSeconds(2)))
            {
                // A lost race with the in-flight debounced write: the wait
                // timed out. Without this line the dropped edit left no trace.
                Log("[SettingsHost] Pending save flush timed out after 2s; last edit may be lost");
            }
        }
        catch (Exception ex)
        {
            Log($"[SettingsHost] Pending save flush failed: {ex.Message}");
        }

        try
        {
            _settingsHostClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log($"[SettingsHost] IPC dispose failed: {ex.Message}");
        }

        DrainLogQueue();
        Environment.Exit(0);
    }
}
