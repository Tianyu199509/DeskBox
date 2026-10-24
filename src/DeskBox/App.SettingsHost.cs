using System.Diagnostics;

namespace DeskBox;

using DeskBox.Contracts;
using DeskBox.Features.QuickCapture;
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
        SettingsHostTrace("branch-entered");
        Log("[SettingsHost] Constructor branch entered");
        InitializeComponent();
        UnhandledException += (_, eventArgs) =>
        {
            Log($"[SettingsHost] Unhandled exception: {eventArgs.Message}");
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
        await settings.LoadAsync();
        Log("[SettingsHost] Settings loaded");
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
                if (await settings.TryReloadFromDiskAsync())
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
            string previewJson = System.Text.Json.JsonSerializer.Serialize(
                settings.Settings,
                DeskBox.Services.SettingsJsonContext.Default.AppSettings);
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

        SettingsHostTrace("onlaunched-done");
        Log("[SettingsHost] Window shown");
    }

    private SettingsWindowFactoryInput BuildSettingsHostFactoryInput(
        SettingsService settings,
        LocalizationService localization,
        ThemeService theme,
        SettingsIpcClient client)
    {
        // Ten coordinators take SettingsService alone; Todo/Search/QuickCapture
        // take host-side no-op callbacks for the widget/reminder side effects
        // the MAIN process re-applies itself from its own SettingsChanged
        // subscription after our commit lands there.
        var todo = new TodoSettingsCoordinator(settings);
        var featureWidgets = new FeatureWidgetsSettingsCoordinator(settings);
        var everythingConnection = new RemoteSearchConnectionClient(client);
        _settingsHostEverythingConnection = everythingConnection;
        var search = new SearchSettingsCoordinator(
            settings,
            localization,
            getConnection: () => everythingConnection,
            ensureConnection: () => everythingConnection,
            getHotkey: () => null,
            setContentSearchEnabled: _ => { },
            applyWidget: null,
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
            applyWidget: (_, _) => Task.CompletedTask,
            tryEnqueue: action => UiDispatcherQueue.TryEnqueue(() => action()),
            reportError: ex => Log($"[QuickCapture] Settings operation failed: {ex}"));

        var widgetActions = new RemoteSettingsWidgetActions(client, settings);
        var hotkey = new RemoteSettingsHotkeyActions(client);
        return new SettingsWindowFactoryInput(
            settings, theme, localization,
            todo,
            featureWidgets,
            search,
            new RemoteBackupSettings(client),
            new RemoteBackupRestoreActions(client),
            quickCapture,
            new RemoteBackupCommands(client),
            new AppearanceSettingsCoordinator(settings),
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
            new RemoteSettingsAppActions(client),
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
    private void ExitSettingsHost()
    {
        // Flush any debounced (1s) pending save first: the host process is
        // the writer, and dying inside the debounce window used to silently
        // drop the user's last edit (batch R5).
        try
        {
            SettingsHostSettingsService?.FlushPendingSaveAsync().Wait(TimeSpan.FromSeconds(2));
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
