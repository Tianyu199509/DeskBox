namespace DeskBox;

using System.Diagnostics;
using System.Threading;

using DeskBox.Services;

/// <summary>
/// Main-process side of the settings-host lifecycle (batch 3d): every
/// settings entry point (tray, widget menus, toast deep links, bare
/// activation, jump list) routes through here — spawn the host if it is not
/// running, wake the existing one otherwise. The in-process window path
/// stays compiled only for the AOT smoke harness, which drives the window
/// directly inside its single process.
/// </summary>
public partial class App
{
    private Process? _settingsHostProcess;

    // Deep review R13 (D5 adversarial hunt): "process alive" is true the
    // moment Process.Start returns, but the host cannot receive pushes
    // until it has connected, subscribed its event handlers, and built the
    // window — three drop segments that silently ate host.show intents
    // arriving during boot. Intent pushes are therefore queued until the
    // host's host.boot-ready notice arrives.
    private bool _settingsHostBootConfirmed;
    private readonly Queue<(string Method, string? Payload)> _pendingHostIntents = new();

    // Deep review R13 (D5): the hotkey-suspend self-heal on host exit must
    // only fire for the exit of the CURRENT host era — a farewell-era
    // reopen spawns a replacement long before the old process object
    // reports death, and the old exit must not resume hotkeys the new host
    // just suspended for its own recording.
    private int _settingsHostGeneration;

    internal bool IsSettingsHostRunning =>
        _settingsHostProcess is { } host && !host.HasExited;

    /// <summary>
    /// True while a settings-host process exists and its window is up. The
    /// host exits when its window closes, so "process alive" is the
    /// user-visible state for every IsSettingsOpen consumer.
    /// </summary>
    internal bool IsSettingsHostVisible => IsSettingsHostRunning;

    private void ShowSettingsViaHost(
        string? sectionTag,
        string? glanceWidgetId,
        bool oneClickUpdate = false,
        string? installResult = null)
    {
        if (IsShuttingDown)
        {
            return;
        }

        if (!IsSettingsHostRunning)
        {
            StartSettingsHostProcess(BuildSettingsHostArguments(
                sectionTag,
                glanceWidgetId,
                oneClickUpdate,
                installResult));
            return;
        }

        // The host process exists, but until its boot-ready notice the pipe,
        // the event subscriptions, and the window may each not exist yet
        // (deep review R13, D5) — queue the intents and deliver them when
        // the host confirms; after that the pipe is connected and pushes
        // land directly.
        if (!_settingsHostBootConfirmed)
        {
            QueueHostIntent(sectionTag, glanceWidgetId, oneClickUpdate, installResult);
            return;
        }

        PushHostIntent(sectionTag, glanceWidgetId, oneClickUpdate, installResult);
    }

    private void QueueHostIntent(
        string? sectionTag,
        string? glanceWidgetId,
        bool oneClickUpdate,
        string? installResult)
    {
        // Navigation first, then the payload-bearing intents, preserving
        // arrival order. The cap guards against a host that never confirms
        // (crashed mid-boot) while the user keeps clicking: oldest intents
        // drop first, exactly like a bounded event queue.
        if (_pendingHostIntents.Count >= 64)
        {
            _ = _pendingHostIntents.Dequeue();
            Log("[SettingsHost] Boot-intent queue full; dropped the oldest intent");
        }

        if (glanceWidgetId is not null)
        {
            _pendingHostIntents.Enqueue((SettingsIpcMethods.HostShowGlance, glanceWidgetId));
        }
        else
        {
            _pendingHostIntents.Enqueue((SettingsIpcMethods.HostShow, sectionTag));
        }

        if (oneClickUpdate)
        {
            _pendingHostIntents.Enqueue((SettingsIpcMethods.HostOneClickUpdate, null));
        }

        if (!string.IsNullOrEmpty(installResult))
        {
            _pendingHostIntents.Enqueue((SettingsIpcMethods.HostQueueUpdateResult, installResult));
        }

        Log($"[SettingsHost] Host still booting; queued intent ({_pendingHostIntents.Count} pending)");
    }

    private void PushHostIntent(
        string? sectionTag,
        string? glanceWidgetId,
        bool oneClickUpdate,
        string? installResult)
    {
        if (glanceWidgetId is not null)
        {
            _ = PushSettingsIpcEventAsync(SettingsIpcMethods.HostShowGlance, glanceWidgetId);
        }
        else
        {
            _ = PushSettingsIpcEventAsync(SettingsIpcMethods.HostShow, sectionTag);
        }

        if (oneClickUpdate)
        {
            _ = PushSettingsIpcEventAsync(SettingsIpcMethods.HostOneClickUpdate, null);
        }

        if (!string.IsNullOrEmpty(installResult))
        {
            _ = PushSettingsIpcEventAsync(SettingsIpcMethods.HostQueueUpdateResult, installResult);
        }
    }

    /// <summary>
    /// host.boot-ready arrived (deep review R13, D5): the host's pipe,
    /// subscriptions, and window are live — flush everything queued during
    /// boot, in order. Runs on the IPC dispatch pool thread; the queue and
    /// flag are only touched here and from the UI thread before the host
    /// connects (the host cannot confirm before the UI thread's spawn
    /// returns), so the hand-off is race-free in practice.
    /// </summary>
    private void OnSettingsHostBootReady()
    {
        _settingsHostBootConfirmed = true;
        int flushed = 0;
        while (_pendingHostIntents.Count > 0)
        {
            (string method, string? payload) = _pendingHostIntents.Dequeue();
            _ = PushSettingsIpcEventAsync(method, payload);
            flushed++;
        }

        if (flushed > 0)
        {
            Log($"[SettingsHost] Boot confirmed; flushed {flushed} queued intent(s)");
        }
    }

    /// <summary>
    /// The host command line: the launch argument pair plus the initial
    /// deep link (a section tag or a glance widget id) and any spawn intent
    /// (one-click update, installer result). Everything beyond the pid rides
    /// the command line because the host cannot receive pushed events
    /// before its pipe connects, and callers do not wait for that.
    /// </summary>
    private static List<string> BuildSettingsHostArguments(
        string? sectionTag,
        string? glanceWidgetId,
        bool oneClickUpdate = false,
        string? installResult = null)
    {
        var arguments = new List<string>
        {
            "--settings",
            Environment.ProcessId.ToString(),
        };
        if (glanceWidgetId is not null)
        {
            arguments.Add("--settings-glance");
            arguments.Add(glanceWidgetId);
        }
        else if (!string.IsNullOrEmpty(sectionTag))
        {
            arguments.Add("--settings-section");
            arguments.Add(sectionTag);
        }

        if (oneClickUpdate)
        {
            arguments.Add("--settings-one-click-update");
        }

        if (!string.IsNullOrEmpty(installResult))
        {
            arguments.Add("--settings-install-result");
            arguments.Add(installResult);
        }

        return arguments;
    }

    /// <summary>Spawns the settings-host process; see BuildSettingsHostArguments.</summary>
    private void StartSettingsHostProcess(List<string> arguments)
    {
        try
        {
            string exePath = Environment.ProcessPath ??
                Path.Combine(AppContext.BaseDirectory, "DeskBox.exe");
            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            _settingsHostProcess = Process.Start(startInfo);
            Log("[SettingsHost] Spawned pid=" + (_settingsHostProcess?.Id.ToString() ?? "null"));
            if (_settingsHostProcess is { } spawnedHost)
            {
                // New era: boot confirmation starts pending, and the exit
                // guard below only self-heals for THIS era (deep review R13,
                // D5 — see OnSettingsHostProcessExited).
                _settingsHostBootConfirmed = false;
                _settingsHostStartTime = TryGetStartTime(spawnedHost);
                int generation = ++_settingsHostGeneration;
                spawnedHost.EnableRaisingEvents = true;
                void ExitHandler(object? sender, EventArgs args)
                {
                    if (sender is Process host)
                    {
                        host.Exited -= ExitHandler;
                    }

                    OnSettingsHostProcessExited(generation);
                }

                spawnedHost.Exited += ExitHandler;
            }
        }
        catch (Exception ex)
        {
            Log($"[SettingsHost] Spawn failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A settings-host that dies mid-hotkey-recording never sends the
    /// resume; the global hotkey would stay suspended until the next
    /// settings change. Watch the process and self-heal (batch R3).
    /// Deep review R13 (D5): the guard is era-scoped — when a farewell-era
    /// reopen spawned a replacement before the old process reported death,
    /// the old exit must not resume hotkeys the NEW host suspended for its
    /// own recording session. A host that crashed mid-boot with intents
    /// queued loses them (they were queued, not pushed); the next open
    /// spawns fresh, which is the pre-R13 behavior for every intent.
    /// </summary>
    private void OnSettingsHostProcessExited(int generation)
    {
        try
        {
            if (generation != _settingsHostGeneration)
            {
                Log("[SettingsHost] Exit of a superseded host era ignored (a newer host was spawned)");
                return;
            }

            if (GlobalHotkeyService is { IsSuspendedForRecording: true } hotkey)
            {
                Log("[SettingsHost] Host died while hotkeys suspended; resuming");
                UiDispatcherQueue?.TryEnqueue(hotkey.ResumeAfterRecording);
            }
        }
        catch (Exception ex)
        {
            Log($"[SettingsHost] Exit guard failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The settings host announced its own exit (host.farewell, batch R11).
    /// "Process alive" is the spawn-vs-show criterion, and the exit flush
    /// keeps the process alive for up to ~2s after the window closed — an
    /// open-settings inside that window pushed HostShow onto the dying
    /// host and was silently lost. Flip the decision back to spawn; keep
    /// the process reference so shutdown can still hard-kill a hung exit.
    /// </summary>
    /// <param name="senderPid">
    /// The farewell carries the sender's own pid (re-review of R11+R12): a
    /// late farewell from an OLD host must never evict a freshly spawned
    /// replacement — without the pin, a delayed goodbye during the
    /// close-reopen race unregistered the new host and cost the next open.
    /// </param>
    internal void MarkSettingsHostExiting(uint senderPid)
    {
        try
        {
            Process? current = _settingsHostProcess;
            if (current is not null && (uint)current.Id == senderPid)
            {
                // Deep review R13 (D5): this runs on the IPC dispatch pool
                // thread while the UI thread may be spawning a replacement.
                // A plain check-then-act could read the OLD host, let the UI
                // thread install the NEW one, and then null the field —
                // evicting the fresh host from the registry (its pipe client
                // would then be rejected by the identity gate and the host
                // would flash-exit). The CAS makes "confirm it is the
                // sender" and "evict exactly that one" one atomic step.
                Process? exchanged = Interlocked.CompareExchange(
                    ref _settingsHostProcess, null, current);
                if (ReferenceEquals(exchanged, current))
                {
                    _exitingSettingsHost = current;
                    // Query the dying Process object itself rather than
                    // pairing the (racy) spawn-time field: a respawn racing
                    // this branch could otherwise stamp the NEW host's start
                    // time onto the OLD host's dying identity.
                    _exitingSettingsHostStartTime = TryGetStartTime(current);
                    Log("[SettingsHost] Farewell received; treating host as not running for show decisions");
                    return;
                }

                // The UI thread swapped in a new host between the read and
                // the CAS — leave the replacement registered; this farewell
                // belongs to the superseded era.
                Log("[SettingsHost] Farewell raced a replacement spawn; new host stays registered");
                return;
            }

            if (_exitingSettingsHost is { } dying && (uint)dying.Id == senderPid)
            {
                return;
            }

            Log($"[SettingsHost] Stale farewell from pid {senderPid} ignored (sender matches no registered host)");
        }
        catch (Exception ex)
        {
            Log($"[SettingsHost] Farewell handling failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The pipe identity gate (batch R12, hardened in deep review R13):
    /// pid + spawn-time creation stamp of the settings host this process
    /// spawned, or of the dying one still saying goodbye. The start time
    /// closes the pid-reuse hole (a recycled pid belongs to a different
    /// process with a different creation time). Zero pid disables the gate
    /// (loopback tests).
    /// </summary>
    internal (uint Pid, DateTime? StartTime) SettingsHostPipeClientIdentity =>
        _settingsHostProcess is { } host
            ? ((uint)host.Id, _settingsHostStartTime)
            : _exitingSettingsHost is { } dying
                ? ((uint)dying.Id, _exitingSettingsHostStartTime)
                : (0, null);

    private Process? _exitingSettingsHost;
    private DateTime? _settingsHostStartTime;
    private DateTime? _exitingSettingsHostStartTime;

    private static DateTime? TryGetStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception)
        {
            // Already-exited or access-denied: the pid-only contract stays.
            return null;
        }
    }

    /// <summary>
    /// Shutdown step (batch 3d): tell the host to close, give it a short
    /// grace window, then hard-terminate — mirroring the updater's pattern.
    /// </summary>
    private void ShutdownSettingsHost()
    {
        Process? host = _settingsHostProcess ?? _exitingSettingsHost;
        if (host is null || host.HasExited)
        {
            return;
        }

        _ = PushSettingsIpcEventAsync(SettingsIpcMethods.HostShutdown, null);
        try
        {
            if (!host.WaitForExit(4000))
            {
                host.Kill();
                host.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            Log($"[SettingsHost] Shutdown wait failed: {ex.Message}");
        }

        _settingsHostProcess = null;
        _exitingSettingsHost = null;
    }
}
