// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
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

        if (s_startupLaunchQuietExit)
        {
            // Task-triggered launch: nobody is watching, and the logon task's
            // RestartOnFailure policy retries in a minute. A modal dialog here
            // would sit on the unattended desktop holding the mutex — the
            // exact zombie shape this path exists to prevent.
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
            Win32Helper.ShowFatalError(
                "DeskBox could not finish starting and is about to close.\n\n" +
                $"Details were written to the log:\n{LogPath}\n\n" +
                "You can start DeskBox again in a moment.",
                "DeskBox");
        }
        catch (Exception dialogFailure)
        {
            Log($"[Startup] Fatal dialog failed: {dialogFailure.Message}");
        }

        DrainLogQueue();
        Environment.Exit(1);
    }

    /// <summary>
    /// The process must end startup owning at least one surface the user can
    /// act on: the tray icon, or a widget window (widget windows legitimately
    /// exist while hidden, so visibility is not the criterion). When only the
    /// tray retry is still pending, this waits it out: killing the launch
    /// inside the early-logon window would turn a recoverable boot race into
    /// "autostart never shows".
    /// </summary>
    private async Task EnsureStartupProducedUsableSurfaceAsync()
    {
        if (!IsStartupLifelineEstablished &&
            WidgetManager?.LoadedWidgetCount == 0 &&
            _trayIconCreationTask is { } trayCreation)
        {
            while (!trayCreation.IsCompleted)
            {
                await Task.Delay(500);
            }
        }

        if (IsStartupLifelineEstablished)
        {
            return;
        }

        if (WidgetManager?.LoadedWidgetCount > 0)
        {
            // Widgets alone are a usable surface: a boot race that only broke
            // the tray icon must not kill a launch that already restored its
            // boxes, and marking the lifeline here also retires the watchdog.
            Log("[Startup] Tray surface unavailable; widget surfaces carry the session");
            MarkStartupLifelineEstablished();
            return;
        }

        FailStartup("no usable tray or widget surface was created", exception: null);
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
}
