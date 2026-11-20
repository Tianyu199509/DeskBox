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
    private readonly object _pendingHostIntentsGate = new();
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

    /// <summary>
    /// Out-of-process settings lives in a second <c>DeskBox.exe --settings</c> process with its
    /// own private heap and working set. Only block main-process memory
    /// cleanup / working-set trim while the host is actively recording a
    /// hotkey (hotkeys suspended on the main process).
    /// </summary>
    internal bool IsSettingsHostBlockingMemoryCleanup =>
        IsSettingsHostRunning &&
        (GlobalHotkeyService?.IsSuspendedForRecording == true ||
         _searchHotkeyService?.IsSuspendedForRecording == true ||
         _clipboardHotkeyService?.IsSuspendedForRecording == true);

    internal bool IsInProcessDeskBoxWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        Platform.Win32Helper.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return true;
        }

        if (BelongsToSettingsHostProcess(processId) || BelongsToStudioHostProcess(processId))
        {
            return false;
        }

        return IsDeskBoxWindow(hwnd);
    }

    internal bool BelongsToSettingsHostProcess(uint processId)
    {
        if (processId == 0)
        {
            return false;
        }

        try
        {
            if (_settingsHostProcess is { } host && (uint)host.Id == processId)
            {
                return true;
            }

            if (_exitingSettingsHost is { } dying && (uint)dying.Id == processId)
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
        }

        return false;
    }

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
        lock (_pendingHostIntentsGate)
        {
            if (!_settingsHostBootConfirmed)
            {
                QueueHostIntentLocked(sectionTag, glanceWidgetId, oneClickUpdate, installResult);
                return;
            }
        }

        PushHostIntent(sectionTag, glanceWidgetId, oneClickUpdate, installResult);
    }

    private void QueueHostIntentLocked(
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
        TryGrantSettingsHostForegroundRight(_settingsHostProcess);
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
    /// boot, in order.
    /// </summary>
    private void OnSettingsHostBootReady()
    {
        List<(string Method, string? Payload)>? toFlush = null;
        lock (_pendingHostIntentsGate)
        {
            _settingsHostBootConfirmed = true;
            if (_pendingHostIntents.Count > 0)
            {
                toFlush = [.. _pendingHostIntents];
                _pendingHostIntents.Clear();
            }
        }

        if (toFlush is { Count: > 0 })
        {
            TryGrantSettingsHostForegroundRight(_settingsHostProcess);
            foreach ((string method, string? payload) in toFlush)
            {
                _ = PushSettingsIpcEventAsync(method, payload);
            }

            Log($"[SettingsHost] Boot confirmed; flushed {toFlush.Count} queued intent(s)");
        }
    }

    private static void TryGrantSettingsHostForegroundRight(Process? host)
    {
        if (host is null)
        {
            return;
        }

        try
        {
            if (!host.HasExited)
            {
                _ = Platform.Win32Helper.AllowSetForegroundWindow((uint)host.Id);
            }
        }
        catch
        {
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
            // Single executable, two roles: the host is this same image
            // relaunched with --settings <mainPid>. Native AOT links all code
            // into each .exe, so a dedicated settings executable would ship a
            // second copy of the whole app; the process boundary alone is what
            // returns the settings UI memory when the window closes.
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

            Process? previousHost = Interlocked.Exchange(ref _settingsHostProcess, null);
            if (previousHost is not null && !ReferenceEquals(previousHost, _exitingSettingsHost))
            {
                try { previousHost.Dispose(); } catch { }
            }

            if (_exitingSettingsHost is { } staleExiting)
            {
                bool hasExited = false;
                try { hasExited = staleExiting.HasExited; } catch { hasExited = true; }
                if (hasExited &&
                    ReferenceEquals(Interlocked.CompareExchange(ref _exitingSettingsHost, null, staleExiting), staleExiting))
                {
                    try { staleExiting.Dispose(); } catch { }
                }
            }

            lock (_pendingHostIntentsGate)
            {
                _settingsHostBootConfirmed = false;
                _pendingHostIntents.Clear();
            }
            Volatile.Write(ref _cachedSettingsHostWindowHandle, IntPtr.Zero);

            Process? spawnedHost = Process.Start(startInfo);
            Log("[SettingsHost] Spawned pid=" + (spawnedHost?.Id.ToString() ?? "null"));
            if (spawnedHost is not null)
            {
                // Initialize start time BEFORE publishing _settingsHostProcess so
                // SettingsHostPipeClientIdentity never pairs a new pid with a stale StartTime.
                _settingsHostStartTime = TryGetStartTime(spawnedHost);
                Volatile.Write(ref _settingsHostProcess, spawnedHost);
                TryGrantSettingsHostForegroundRight(spawnedHost);
                int generation = ++_settingsHostGeneration;
                spawnedHost.EnableRaisingEvents = true;
                void ExitHandler(object? sender, EventArgs args)
                {
                    if (sender is Process host)
                    {
                        host.Exited -= ExitHandler;
                        OnSettingsHostProcessExited(generation);
                        if (ReferenceEquals(Interlocked.CompareExchange(ref _settingsHostProcess, null, host), host) ||
                            ReferenceEquals(Interlocked.CompareExchange(ref _exitingSettingsHost, null, host), host))
                        {
                            try { host.Dispose(); } catch { }
                        }
                    }
                    else
                    {
                        OnSettingsHostProcessExited(generation);
                    }
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

            if (GlobalHotkeyService?.IsSuspendedForRecording == true ||
                _searchHotkeyService?.IsSuspendedForRecording == true ||
                _clipboardHotkeyService?.IsSuspendedForRecording == true)
            {
                Log("[SettingsHost] Host died while hotkeys suspended; resuming");
                UiDispatcherQueue?.TryEnqueue(() =>
                {
                    GlobalHotkeyService?.ResumeAfterRecording();
                    // Deep review R13-2 (D10 audit): the recorder suspends
                    // ALL active hotkey services (see InProcessSettingsHotkeyActions),
                    // so resume search and clipboard hotkeys alongside main.
                    _searchHotkeyService?.ResumeAfterRecording();
                    _clipboardHotkeyService?.ResumeAfterRecording();
                });
            }

            NotifySettingsHostClosedForMemoryCleanup();
        }
        catch (Exception ex)
        {
            Log($"[SettingsHost] Exit guard failed: {ex.Message}");
        }
    }

    private void NotifySettingsHostClosedForMemoryCleanup()
    {
        if (IsShuttingDown)
        {
            return;
        }

        UiDispatcherQueue?.TryEnqueue(() =>
        {
            if (IsShuttingDown || IsSettingsHostVisible)
            {
                return;
            }

            NotifyMemoryCleanupActivity();
            if (WidgetManager?.CaptureMemoryCleanupVisibilitySnapshot().HasNativeVisibleWidgets == false)
            {
                ScheduleBackgroundMemoryCleanup("settings-host-closed");
            }
        });
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
                    Volatile.Write(ref _cachedSettingsHostWindowHandle, IntPtr.Zero);
                    _exitingSettingsHost = current;
                    // Query the dying Process object itself rather than
                    // pairing the (racy) spawn-time field: a respawn racing
                    // this branch could otherwise stamp the NEW host's start
                    // time onto the OLD host's dying identity.
                    _exitingSettingsHostStartTime = TryGetStartTime(current);
                    Log("[SettingsHost] Farewell received; treating host as not running for show decisions");
                    NotifySettingsHostClosedForMemoryCleanup();
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
    private IntPtr _cachedSettingsHostWindowHandle;

    internal IntPtr TryGetVisibleSettingsHostProcessWindowHandle()
    {
        if (_settingsHostProcess is not { } host)
        {
            return IntPtr.Zero;
        }

        uint targetPid;
        try
        {
            if (host.HasExited)
            {
                return IntPtr.Zero;
            }

            targetPid = (uint)host.Id;
        }
        catch
        {
            return IntPtr.Zero;
        }

        if (targetPid == 0)
        {
            return IntPtr.Zero;
        }

        IntPtr cached = Volatile.Read(ref _cachedSettingsHostWindowHandle);
        if (IsVisibleNonEmptyProcessWindow(cached, targetPid))
        {
            return cached;
        }

        IntPtr discovered = FindVisibleTopLevelWindowForProcess(targetPid);
        Volatile.Write(ref _cachedSettingsHostWindowHandle, discovered);
        return discovered;
    }

    internal IntPtr ResolveStoreUpdateOwnerWindowHandle()
    {
        IntPtr inProcessVisible = GetVisibleSettingsWindowHandle();
        if (IsVisibleNonEmptyOwnerWindow(inProcessVisible))
        {
            return inProcessVisible;
        }

        IntPtr hostSettingsHwnd = TryGetVisibleSettingsHostProcessWindowHandle();
        if (hostSettingsHwnd != IntPtr.Zero)
        {
            return hostSettingsHwnd;
        }

        IntPtr studioHwnd = GetStudioHostWindowHandle();
        if (IsVisibleNonEmptyOwnerWindow(studioHwnd))
        {
            return studioHwnd;
        }

        IntPtr visibleDeskBoxHwnd = FindVisibleTopLevelWindowForProcess(
            (uint)Environment.ProcessId,
            excludeHwnd: TrayHostWindowHandle);
        if (visibleDeskBoxHwnd != IntPtr.Zero)
        {
            return visibleDeskBoxHwnd;
        }

        IntPtr inProcessSettingsAny = GetAnySettingsHostWindowHandle();
        if (IsVisibleNonEmptyOwnerWindow(inProcessSettingsAny))
        {
            return inProcessSettingsAny;
        }

        IntPtr foreground = Platform.Win32Helper.GetForegroundWindow();
        if (IsVisibleNonEmptyOwnerWindow(foreground))
        {
            return foreground;
        }

        IntPtr shell = Platform.Win32Helper.GetShellWindow();
        if (shell != IntPtr.Zero && Platform.Win32Helper.IsWindow(shell) && Platform.Win32Helper.IsWindowVisible(shell))
        {
            return shell;
        }

        return RecreateFolderPickerHelperWindow();
    }

    private static bool IsVisibleNonEmptyOwnerWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero ||
            !Platform.Win32Helper.IsWindow(hwnd) ||
            !Platform.Win32Helper.IsWindowVisible(hwnd) ||
            Platform.Win32Helper.IsWindowCloaked(hwnd))
        {
            return false;
        }

        return Platform.Win32Helper.GetWindowRect(hwnd, out Platform.Win32Helper.RECT rect) &&
               (rect.Right - rect.Left) > 0 &&
               (rect.Bottom - rect.Top) > 0;
    }

    private static bool IsVisibleNonEmptyProcessWindow(IntPtr hwnd, uint expectedPid)
    {
        if (!IsVisibleNonEmptyOwnerWindow(hwnd))
        {
            return false;
        }

        Platform.Win32Helper.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == expectedPid;
    }

    private static IntPtr FindVisibleTopLevelWindowForProcess(uint targetPid, IntPtr excludeHwnd = default)
    {
        IntPtr found = IntPtr.Zero;
        Platform.Win32Helper.EnumWindows((hWnd, _) =>
        {
            if (hWnd == IntPtr.Zero || hWnd == excludeHwnd)
            {
                return true;
            }

            Platform.Win32Helper.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != targetPid)
            {
                return true;
            }

            if (!IsVisibleNonEmptyOwnerWindow(hWnd))
            {
                return true;
            }

            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

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
        if (host is null)
        {
            return;
        }

        bool alreadyExited = false;
        try { alreadyExited = host.HasExited; } catch { alreadyExited = true; }
        if (!alreadyExited)
        {
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
        }

        Process? p1 = Interlocked.Exchange(ref _settingsHostProcess, null);
        Process? p2 = Interlocked.Exchange(ref _exitingSettingsHost, null);
        try { p1?.Dispose(); } catch { }
        if (!ReferenceEquals(p2, p1))
        {
            try { p2?.Dispose(); } catch { }
        }
    }
}
