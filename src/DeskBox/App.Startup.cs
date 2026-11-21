// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox;

/// <summary>
/// Startup resilience. The single-instance mutex is taken in the App
/// constructor, long before any window exists, so a startup that fails or
/// hangs must never leave the process running: it would own the lock while
/// offering no UI, and every later launch would just signal it and exit.
/// Fatal failures therefore always terminate the process, and a dedicated
/// watchdog covers the case where startup stops making progress instead of
/// throwing.
/// </summary>
public partial class App
{
    /// <summary>Startup must reach a usable tray surface within this window.</summary>
    private const int StartupWatchdogStallMs = 90_000;

    /// <summary>How long the fatal dialog may stay up before the process exits anyway.</summary>
    private const int StartupFailureGraceMs = 20_000;

    private static int s_startupLifelineEstablished;
    private static int s_startupFailureHandled;
    private static int s_startupWatchdogArmed;
    private static bool s_startupLaunchQuietExit;
    private static long s_lastStartupProgressTicks;

    /// <summary>
    /// True once the tray surface is usable. Before that point the process has
    /// nothing the user can act on, so exceptions and stalls are fatal.
    /// </summary>
    private static bool IsStartupLifelineEstablished =>
        Volatile.Read(ref s_startupLifelineEstablished) != 0;

    /// <summary>
    /// Records that startup advanced to a new phase. The watchdog measures
    /// time since the last mark, not total elapsed time: legitimately long
    /// phases (a multi-gigabyte data restore before the tray exists) keep
    /// marking progress and must not be killed, while a real hang shows no
    /// marks at all.
    /// </summary>
    internal static void MarkStartupProgress()
    {
        Volatile.Write(ref s_lastStartupProgressTicks, Environment.TickCount64);
    }

    /// <summary>
    /// Arms the watchdog thread that terminates a startup which stops making
    /// progress. A dedicated thread, not the pool: startup work (snapshot
    /// zip, schtasks, WMI queries) can saturate the pool, and the whole point
    /// is to fire when the rest of the process is stuck.
    /// </summary>
    private static void StartStartupWatchdog()
    {
        if (Interlocked.Exchange(ref s_startupWatchdogArmed, 1) != 0)
        {
            return;
        }

        MarkStartupProgress();

        var watchdog = new Thread(() =>
        {
            while (true)
            {
                if (IsStartupLifelineEstablished)
                {
                    return;
                }

                Thread.Sleep(500);
                long stalledMs = Environment.TickCount64 -
                    Volatile.Read(ref s_lastStartupProgressTicks);
                if (stalledMs >= StartupWatchdogStallMs)
                {
                    if (!IsStartupLifelineEstablished)
                    {
                        FailStartup(
                            $"startup made no progress for {stalledMs} ms",
                            exception: null);
                    }

                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "DeskBox startup watchdog"
        };

        watchdog.Start();
    }

    private static void MarkStartupLifelineEstablished()
    {
        Volatile.Write(ref s_startupLifelineEstablished, 1);
    }

    /// <summary>
    /// Reports a fatal startup failure and terminates the process. The
    /// single-instance mutex is deliberately left to the kernel: releasing it
    /// early would let a second instance start while this one is still writing
    /// logs and settings.
    /// </summary>
    private static void FailStartup(string reason, Exception? exception)
    {
        // The message box pumps messages, so the fatal path can be re-entered
        // from an unhandled exception while the dialog is up. Only the first
        // entry reports and exits.
        if (Interlocked.Exchange(ref s_startupFailureHandled, 1) != 0)
        {
            return;
        }

        Log($"[Startup] Fatal: {reason}" +
            (exception is null ? string.Empty : $": {exception}"));

        // A fatal-startup exit is intentional: closing the breadcrumb keeps
        // the next launch from mistaking this exit for a storm casualty.
        Current?._startupSessionLedger?.MarkCleanExit();

        if (s_startupLaunchQuietExit)
        {
            // Background autostart launch: nobody is actively waiting on a modal
            // dialog, and showing one on an unattended logon desktop would hold
            // the single-instance mutex hostage. Exit cleanly so a later manual
            // launch can start fresh.
            DrainLogQueue();
            Environment.Exit(1);
        }

        var exitThread = new Thread(() =>
        {
            Thread.Sleep(StartupFailureGraceMs);
            DrainLogQueue();
            Environment.Exit(1);
        })
        {
            IsBackground = true,
            Name = "DeskBox fatal startup exit"
        };

        exitThread.Start();

        try
        {
            Win32Helper.ShowFatalError(BuildFatalStartupMessage(), "DeskBox");
        }
        catch (Exception dialogFailure)
        {
            Log($"[Startup] Fatal dialog failed: {dialogFailure.Message}");
        }

        DrainLogQueue();
        Environment.Exit(1);
    }

    /// <summary>
    /// Localized fatal-startup message, with the diagnosed environment
    /// causes appended when the shell relationship or compatibility layers
    /// explain the broken startup. Falls back to the historical English text
    /// when no localization is reachable yet, so the fatal path can never
    /// fail to produce a dialog.
    /// </summary>
    private static string BuildFatalStartupMessage()
    {
        LocalizationService? localization = Current?.LocalizationService;
        if (localization is null)
        {
            return "DeskBox could not finish starting and is about to close.\n\n" +
                $"Details were written to the log:\n{LogPath}\n\n" +
                "You can start DeskBox again in a moment.";
        }

        string message = localization.Format("Startup.Fatal.Body", LogPath);
        try
        {
            string environmentIssues =
                DragDropPermissionService.BuildStartupEnvironmentIssueSummary(localization);
            if (environmentIssues.Length > 0)
            {
                message += "\n\n" +
                    localization.T("Startup.Fatal.EnvironmentIssuesHeader") + "\n" +
                    environmentIssues;
            }
        }
        catch (Exception summaryFailure)
        {
            // The fatal dialog must survive its own diagnostics.
            Log($"[Startup] Environment issue summary failed: {summaryFailure.Message}");
        }

        return message;
    }

    /// <summary>
    /// The process must end startup owning at least one surface the user can
    /// act on: the tray icon, a widget window (widget windows legitimately
    /// exist while hidden, so visibility is not the criterion), or a
    /// registered global hotkey (F7 reveal; a bare launch opens settings).
    /// When only the tray retry is still pending, this waits it out: killing
    /// the launch inside the early-logon window would turn a recoverable
    /// boot race into "autostart never shows".
    /// </summary>
    private async Task EnsureStartupProducedUsableSurfaceAsync()
    {
        Task<bool>? trayCreation = _trayIconCreationTask;
        if (!IsStartupLifelineEstablished &&
            WidgetManager?.LoadedWidgetCount == 0 &&
            trayCreation is { } pendingTrayCreation)
        {
            while (!pendingTrayCreation.IsCompleted)
            {
                await Task.Delay(500);
            }
        }

        if (IsStartupLifelineEstablished)
        {
            WatchShellAboveAppTrayFailure(trayCreation);
            return;
        }

        if (IsTrayIconHiddenByUserSetting())
        {
            // A deliberately hidden tray is not a lost surface: the tray host
            // window (hotkey owner) exists, widgets are typically visible, and
            // a bare relaunch opens settings — so this session must not be
            // judged by the "no usable surface" fatal path even when the user
            // also removed every widget and disabled the hotkey. The
            // elevation notice is skipped for the same reason: the missing
            // icon is a preference, not an environment failure.
            Log("[Startup] Tray icon hidden by user setting; tray host carries the session");
            MarkStartupLifelineEstablished();
            return;
        }

        if (WidgetManager?.LoadedWidgetCount > 0)
        {
            // Widgets alone are a usable surface: a boot race that only broke
            // the tray icon must not kill a launch that already restored its
            // boxes, and marking the lifeline here also retires the watchdog.
            Log("[Startup] Tray surface unavailable; widget surfaces carry the session");
            MarkStartupLifelineEstablished();
            WatchShellAboveAppTrayFailure(trayCreation);
            return;
        }

        // A registered global hotkey keeps an interactive entry alive even
        // with the tray gone. Treating that session as fatal turns an
        // environment quirk (UIPI blocks Shell_NotifyIcon under an elevated
        // desktop) into "the app never opens" on every launch; degrade
        // instead, and let the shell-elevation notice explain the tray.
        if (GlobalHotkeyService?.IsRegistered == true)
        {
            Log("[Startup] Tray surface unavailable; global hotkey carries the session");
            MarkStartupLifelineEstablished();
            WatchShellAboveAppTrayFailure(trayCreation);
            return;
        }

        FailStartup("no usable tray or widget surface was created", exception: null);
    }

    /// <summary>
    /// When the shell runs above this process's integrity, a lost tray has
    /// an environmental cause worth explaining to the user. No-op on
    /// mismatch-free sessions or healthy trays; the notice helper itself
    /// bails out if the tray retry eventually succeeds.
    /// </summary>
    private void WatchShellAboveAppTrayFailure(Task<bool>? trayCreation)
    {
        if (trayCreation is null ||
            _startupElevationAssessment.Mismatch != ShellIntegrityMismatch.ShellAboveApp)
        {
            return;
        }

        _ = ShowShellElevationNoticeWhenTrayFailsAsync(trayCreation);
    }

    /// <summary>
    /// Runs a startup step that is not part of the lifeline. A failure degrades
    /// to a log line so the app stays usable instead of refusing to start.
    /// </summary>
    private static async Task RunOptionalStartupStepAsync(string name, Func<Task> step) =>
        await EnsureStartupPipeline().RunOptionalAsync(name, step);

    private static void RunOptionalStartupStep(string name, Action step) =>
        EnsureStartupPipeline().RunOptional(name, step);

    /// <summary>
    /// Runs a startup step that is part of the lifeline. A failure is recorded
    /// in the pipeline report, then rethrown so the launch's fatal path keeps
    /// its existing semantics.
    /// </summary>
    private static async Task RunCriticalStartupStepAsync(string name, Func<Task> step) =>
        await EnsureStartupPipeline().RunCriticalAsync(name, step);

    private static void RunCriticalStartupStep(string name, Action step) =>
        EnsureStartupPipeline().RunCritical(name, step);

    /// <summary>
    /// Records a degradation handled by a bespoke recovery path — one that needs
    /// its own cleanup and cannot be expressed as a single step call (the widget
    /// restore path must unwind the desktop-layer deferral on failure).
    /// </summary>
    private static void RecordStartupDegradation(string name, string? diagnostic) =>
        EnsureStartupPipeline().RecordDegraded(name, diagnostic);

    /// <summary>The startup step results collected so far, for diagnostics.</summary>
    internal static IReadOnlyList<StartupStepResult> StartupStepResults =>
        EnsureStartupPipeline().Snapshot();

    private static StartupPipeline? s_startupPipeline;

    private static StartupPipeline EnsureStartupPipeline() =>
        s_startupPipeline ??= new StartupPipeline(Log, MarkStartupProgress);

    internal const string ApplicationRestartCommandLine = "--startup --startup-source=restart";
    private int _applicationRestartRegistered;

    /// <summary>
    /// Runs once after primary launch: applies the one-time autostart default
    /// for genuinely new installs, and keeps Windows Application Restart (ARSO)
    /// synchronized with the live OS autostart registration. Development data
    /// roots (portable runs, smoke harnesses) never touch machine-level startup
    /// registration or ARSO.
    /// </summary>
    private async Task InitializeAutoStartOnLaunchAsync()
    {
        try
        {
            if (DeskBoxDataPathService.Current.IsDevelopmentRoot)
            {
                return;
            }

            if (AutoStartDefaultPolicy.ShouldApply(SettingsService.Settings))
            {
                StartupOperationResult initialStatus =
                    await StartupService.GetStatusAsync().ConfigureAwait(false);
                StartupOperationResult resolved =
                    await AutoStartDefaultPolicy.ResolveAsync(StartupService.Current).ConfigureAwait(false);
                bool enabled = AutoStartDefaultPolicy.IsEnabledState(resolved.State);

                void Commit()
                {
                    SettingsService.Settings.Core.AutoStart = enabled;
                    if (AutoStartDefaultPolicy.ShouldMarkApplied(resolved.State))
                    {
                        SettingsService.Settings.Core.AutoStartDefaultApplied = true;
                    }

                    SettingsService.SaveDebounced();
                }

                DeskBox.Platform.UiDispatch.RunOrDefer(Commit);
                SyncApplicationRestartRegistration(enabled);
                StartupService.NotifyStateChanged();

                Log(
                    $"[AutoStart] One-time default applied: initial={initialStatus.State} " +
                    $"effective={resolved.State} mode={resolved.EffectiveMode} fallback={resolved.UsedFallback} mirror={enabled}");
                return;
            }

            // Do not overwrite Settings.Core.AutoStart on subsequent launches:
            // preserving the saved preference is how Settings detects an
            // external deletion (Core.AutoStart=true while OS state=NotRegistered).
            StartupOperationResult liveStatus =
                await StartupService.GetStatusAsync().ConfigureAwait(false);
            SyncApplicationRestartRegistration(AutoStartDefaultPolicy.IsEnabledState(liveStatus.State));
        }
        catch (Exception ex)
        {
            Log($"[AutoStart] Launch initialization failed: {ex.Message}");
        }
    }

    internal async Task<StartupOperationResult> GetAutoStartStatusAsync()
    {
        StartupOperationResult status = await StartupService.GetStatusAsync().ConfigureAwait(false);
        bool enabled = AutoStartDefaultPolicy.IsEnabledState(status.State);
        SyncApplicationRestartRegistration(enabled);
        // If the user re-enabled DeskBox externally (e.g. in Windows Startup Apps),
        // mirror Core.AutoStart=true. When NotRegistered, intentionally leave a
        // previously true Core.AutoStart intact so Settings can surface the
        // ExternallyRemoved warning.
        if (enabled)
        {
            DeskBox.Platform.UiDispatch.RunOrDefer(() =>
            {
                if (!SettingsService.Settings.Core.AutoStart)
                {
                    SettingsService.Settings.Core.AutoStart = true;
                    SettingsService.SaveDebounced();
                }
            });
        }

        return status;
    }

    internal async Task<StartupOperationResult> SetAutoStartEnabledAsync(bool enabled)
    {
        StartupOperationResult result = await StartupService.SetEnabledAsync(enabled).ConfigureAwait(false);
        bool effectiveEnabled = AutoStartDefaultPolicy.IsEnabledState(result.State);
        if (result.State != StartupRegistrationState.Pending)
        {
            SyncApplicationRestartRegistration(effectiveEnabled);
            DeskBox.Platform.UiDispatch.RunOrDefer(() =>
            {
                if (SettingsService.Settings.Core.AutoStart != effectiveEnabled)
                {
                    SettingsService.Settings.Core.AutoStart = effectiveEnabled;
                    SettingsService.SaveDebounced();
                }
            });
        }

        return result;
    }

    /// <summary>
    /// Registers or unregisters Windows Application Restart (ARSO) as a dual-channel
    /// reboot accelerator alongside the primary Task Scheduler / Store StartupTask
    /// registration. Uses RESTART_NO_CRASH | RESTART_NO_HANG so ARSO only restarts
    /// DeskBox across OS reboots/updates and never interferes with crash handling.
    /// </summary>
    internal void SyncApplicationRestartRegistration(bool enabled)
    {
        if (DeskBoxDataPathService.Current.IsDevelopmentRoot || IsShuttingDown)
        {
            return;
        }

        try
        {
            if (enabled)
            {
                int hr = Kernel32NativeMethods.RegisterApplicationRestart(
                    ApplicationRestartCommandLine,
                    Kernel32NativeMethods.RestartNoCrash | Kernel32NativeMethods.RestartNoHang);
                if (hr >= 0)
                {
                    if (Interlocked.Exchange(ref _applicationRestartRegistered, 1) == 0)
                    {
                        Log("[AutoStart] Registered Windows Application Restart (ARSO) accelerator.");
                    }
                }
                else
                {
                    Log($"[AutoStart] RegisterApplicationRestart failed hr=0x{hr:X8}");
                }
            }
            else if (Interlocked.Exchange(ref _applicationRestartRegistered, 0) != 0)
            {
                int hr = Kernel32NativeMethods.UnregisterApplicationRestart();
                if (hr >= 0)
                {
                    Log("[AutoStart] Unregistered Windows Application Restart (ARSO).");
                }
                else
                {
                    Log($"[AutoStart] UnregisterApplicationRestart failed hr=0x{hr:X8}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[AutoStart] Application restart sync failed: {ex.Message}");
        }
    }

    private void UnregisterApplicationRestartForCleanExit()
    {
        if (Interlocked.Exchange(ref _applicationRestartRegistered, 0) == 0)
        {
            return;
        }

        try
        {
            int hr = Kernel32NativeMethods.UnregisterApplicationRestart();
            if (hr < 0)
            {
                Log($"[Shutdown] UnregisterApplicationRestart failed hr=0x{hr:X8}");
            }
        }
        catch (Exception ex)
        {
            Log($"[Shutdown] UnregisterApplicationRestart threw: {ex.Message}");
        }
    }
}
