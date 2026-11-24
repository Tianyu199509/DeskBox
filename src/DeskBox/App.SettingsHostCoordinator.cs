namespace DeskBox;

using System.Diagnostics;
using System.Threading;

using DeskBox.Contracts;
using DeskBox.Services;
using DeskBox.Views;
using Microsoft.UI.Xaml;
using WinRT.Interop;

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
    private long _settingsHostSpawnedTickMs;
    private SettingsWindow? _inProcessFallbackSettingsWindow;
    private InProcessSettingsHotkeyActions? _inProcessFallbackHotkeyActions;
    private bool _inProcessFallbackPersistenceSubscribed;
    private bool _useInProcessSettingsFallback;
    private (string? SectionTag, string? GlanceWidgetId, bool OneClickUpdate, string? InstallResult) _lastSettingsHostSpawnIntent;

    internal bool IsSettingsHostRunning =>
        _settingsHostProcess is { } host && !host.HasExited;

    internal bool HasInProcessFallbackSettingsWindow =>
        _inProcessFallbackSettingsWindow is not null;

    /// <summary>
    /// True while a settings-host process exists and its window is up, or while
    /// the in-process fallback settings window is visible.
    /// </summary>
    internal bool IsSettingsHostVisible =>
        IsSettingsHostRunning ||
        (_inProcessFallbackSettingsWindow is { IsVisibleToUser: true });

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

        if (_useInProcessSettingsFallback || _settingsIpcServer is null)
        {
            if (!_useInProcessSettingsFallback && _settingsIpcServer is null)
            {
                Log("[SettingsHost] Settings IPC server unavailable; using in-process settings fallback");
                _useInProcessSettingsFallback = true;
            }

            ShowSettingsInProcessFallback(sectionTag, glanceWidgetId, oneClickUpdate, installResult);
            return;
        }

        if (!IsSettingsHostRunning)
        {
            lock (_pendingHostIntentsGate)
            {
                _lastSettingsHostSpawnIntent = (sectionTag, glanceWidgetId, oneClickUpdate, installResult);
            }

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
                WorkingDirectory = AppContext.BaseDirectory,
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

            Volatile.Write(ref _settingsHostSpawnedTickMs, Environment.TickCount64);
            Process? spawnedHost = Process.Start(startInfo);
            Log("[SettingsHost] Spawned pid=" + (spawnedHost?.Id.ToString() ?? "null"));
            if (spawnedHost is null)
            {
                ActivateInProcessSettingsFallback("spawn-returned-null");
                return;
            }

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
                    int? exitCode = null;
                    try { exitCode = host.ExitCode; } catch { }
                    OnSettingsHostProcessExited(generation, exitCode);
                    if (ReferenceEquals(Interlocked.CompareExchange(ref _settingsHostProcess, null, host), host) ||
                        ReferenceEquals(Interlocked.CompareExchange(ref _exitingSettingsHost, null, host), host))
                    {
                        try { host.Dispose(); } catch { }
                    }
                }
                else
                {
                    OnSettingsHostProcessExited(generation, null);
                }
            }

            spawnedHost.Exited += ExitHandler;
            _ = WatchSettingsHostBootTimeoutAsync(generation);
        }
        catch (Exception ex)
        {
            Log($"[SettingsHost] Spawn failed: {ex.Message}");
            ActivateInProcessSettingsFallback("spawn-exception");
        }
    }

    private async Task WatchSettingsHostBootTimeoutAsync(int generation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            if (IsShuttingDown || _useInProcessSettingsFallback || generation != _settingsHostGeneration)
            {
                return;
            }

            bool confirmed;
            lock (_pendingHostIntentsGate)
            {
                confirmed = _settingsHostBootConfirmed;
            }

            if (!confirmed)
            {
                Log($"[SettingsHost] Boot timed out after 12000ms for generation={generation}; switching to in-process settings fallback");
                ActivateInProcessSettingsFallback("boot-timeout");
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// A settings-host that dies mid-hotkey-recording never sends the
    /// resume; the global hotkey would stay suspended until the next
    /// settings change. Watch the process and self-heal (batch R3).
    /// Deep review R13 (D5): the guard is era-scoped — when a farewell-era
    /// reopen spawned a replacement before the old process reported death,
    /// the old exit must not resume hotkeys the NEW host suspended for its
    /// own recording session. If the host exited before confirming boot
    /// (#1254), automatically switch to the in-process settings fallback and
    /// replay the spawn + queued intents so Settings never stays unreachable.
    /// </summary>
    private void OnSettingsHostProcessExited(int generation, int? exitCode = null)
    {
        try
        {
            if (generation != _settingsHostGeneration)
            {
                Log("[SettingsHost] Exit of a superseded host era ignored (a newer host was spawned)");
                return;
            }

            bool wasBootConfirmed;
            lock (_pendingHostIntentsGate)
            {
                wasBootConfirmed = _settingsHostBootConfirmed;
            }

            long spawnedTick = Volatile.Read(ref _settingsHostSpawnedTickMs);
            long elapsedMs = spawnedTick > 0 ? Math.Max(0, Environment.TickCount64 - spawnedTick) : -1;
            string formattedCode = exitCode is int code
                ? $"0x{unchecked((uint)code):X8} ({code})"
                : "unknown";
            Log(
                $"[SettingsHost] Process exited generation={generation} " +
                $"code={formattedCode} bootConfirmed={wasBootConfirmed} elapsedMs={elapsedMs}");

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

            if (!wasBootConfirmed && !IsShuttingDown)
            {
                ActivateInProcessSettingsFallback($"pre-boot-exit:{formattedCode}");
                return;
            }

            NotifySettingsHostClosedForMemoryCleanup();
        }
        catch (Exception ex)
        {
            Log($"[SettingsHost] Exit guard failed: {ex.Message}");
        }
    }

    private void ActivateInProcessSettingsFallback(string reason)
    {
        if (IsShuttingDown)
        {
            return;
        }

        (string? SectionTag, string? GlanceWidgetId, bool OneClickUpdate, string? InstallResult) spawnIntent;
        List<(string Method, string? Payload)> queuedIntents;
        lock (_pendingHostIntentsGate)
        {
            _useInProcessSettingsFallback = true;
            _settingsHostGeneration++;
            spawnIntent = _lastSettingsHostSpawnIntent;
            _lastSettingsHostSpawnIntent = default;
            queuedIntents = [.. _pendingHostIntents];
            _pendingHostIntents.Clear();
        }

        Process? abandonedHost = Interlocked.Exchange(ref _settingsHostProcess, null);
        Volatile.Write(ref _cachedSettingsHostWindowHandle, IntPtr.Zero);
        if (abandonedHost is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (!abandonedHost.HasExited)
                    {
                        abandonedHost.Kill();
                    }
                }
                catch
                {
                }
                finally
                {
                    try { abandonedHost.Dispose(); } catch { }
                }
            });
        }

        Log(
            $"[SettingsHost] Activating in-process settings fallback " +
            $"(reason={reason}, queuedIntents={queuedIntents.Count})");

        void ReplayOnUi()
        {
            if (IsShuttingDown)
            {
                return;
            }

            try
            {
                ShowSettingsInProcessFallback(
                    spawnIntent.SectionTag,
                    spawnIntent.GlanceWidgetId,
                    spawnIntent.OneClickUpdate,
                    spawnIntent.InstallResult);
                foreach ((string method, string? payload) in queuedIntents)
                {
                    ApplyHostIntentToInProcessWindow(method, payload);
                }
            }
            catch (Exception ex)
            {
                Log($"[SettingsHost] In-process settings fallback failed: {ex}");
            }
        }

        if (UiDispatcherQueue is { HasThreadAccess: true })
        {
            ReplayOnUi();
        }
        else
        {
            UiDispatcherQueue?.TryEnqueue(ReplayOnUi);
        }
    }

    private void ShowSettingsInProcessFallback(
        string? sectionTag,
        string? glanceWidgetId,
        bool oneClickUpdate,
        string? installResult)
    {
        if (IsShuttingDown)
        {
            return;
        }

        CancelBackgroundMemoryCleanup();
        SettingsWindow window = _inProcessFallbackSettingsWindow ?? CreateInProcessFallbackSettingsWindow();
        window.ShowWindow();
        if (!string.IsNullOrEmpty(glanceWidgetId))
        {
            window.ShowGlanceSection(glanceWidgetId);
        }
        else if (!string.IsNullOrEmpty(sectionTag))
        {
            window.ShowSection(sectionTag);
            if (string.Equals(sectionTag, "About", StringComparison.Ordinal))
            {
                window.BringUpdateStatusIntoView();
            }
        }

        if (oneClickUpdate)
        {
            _ = window.RunOneClickUpdateFlowAsync();
        }

        if (!string.IsNullOrEmpty(installResult))
        {
            window.QueueUpdateInstallResultDialog(installResult);
        }
    }

    private void ApplyHostIntentToInProcessWindow(string method, string? payload)
    {
        if (_inProcessFallbackSettingsWindow is not { } window)
        {
            return;
        }

        switch (method)
        {
            case SettingsIpcMethods.HostShowGlance when !string.IsNullOrEmpty(payload):
                window.ShowWindow();
                window.ShowGlanceSection(payload);
                break;
            case SettingsIpcMethods.HostShow:
                window.ShowWindow();
                if (!string.IsNullOrEmpty(payload))
                {
                    window.ShowSection(payload);
                    if (string.Equals(payload, "About", StringComparison.Ordinal))
                    {
                        window.BringUpdateStatusIntoView();
                    }
                }
                break;
            case SettingsIpcMethods.HostOneClickUpdate:
                _ = window.RunOneClickUpdateFlowAsync();
                break;
            case SettingsIpcMethods.HostQueueUpdateResult when !string.IsNullOrEmpty(payload):
                window.QueueUpdateInstallResultDialog(payload);
                break;
        }
    }

    private SettingsWindow CreateInProcessFallbackSettingsWindow()
    {
        // Retail Direct builds purge pure Settings.* keys from the resident main process
        // to save memory; re-enable full dictionary loading before constructing the window.
        LocalizationService.DisableResidentHostKeyFilter();
        _inProcessFallbackHotkeyActions ??= new InProcessSettingsHotkeyActions(
            GlobalHotkeyService,
            SearchHotkeyService,
            DesktopDoubleClickActivationService,
            SettingsService,
            searchHotkeyProvider: () => _searchHotkeyService,
            clipboardHotkeyProvider: () => _clipboardHotkeyService,
            onClipboardRuntimeReconcile: ReconcileClipboardFeatureRuntime);
        if (!_inProcessFallbackPersistenceSubscribed)
        {
            _inProcessFallbackPersistenceSubscribed = true;
            SettingsService.LocalPersistCompleted += _ =>
                UiDispatcherQueue?.TryEnqueue(() => _inProcessFallbackSettingsWindow?.DismissSaveFailedBanner());
        }

        var input = new SettingsWindowFactoryInput(
            SettingsService,
            ThemeService,
            LocalizationService,
            _todoSettings ?? throw new InvalidOperationException("Todo settings are not initialized."),
            _featureWidgetsSettings ?? throw new InvalidOperationException("Feature widgets settings are not initialized."),
            _searchSettings ?? throw new InvalidOperationException("Search settings are not initialized."),
            _backupSettings ?? throw new InvalidOperationException("Backup settings are not initialized."),
            _backupRestoreActions ?? throw new InvalidOperationException("Backup restore actions are not initialized."),
            _quickCaptureSettings ?? throw new InvalidOperationException("Quick Capture is not initialized."),
            _backupRuntime ?? throw new InvalidOperationException("Backup runtime is not initialized."),
            _appearanceSettings ?? throw new InvalidOperationException("Appearance settings are not initialized."),
            _capsuleSettings ?? throw new InvalidOperationException("Capsule settings are not initialized."),
            _interactionSettings ?? throw new InvalidOperationException("Interaction settings are not initialized."),
            _fileDisplaySettings ?? throw new InvalidOperationException("File display settings are not initialized."),
            _fileStackSettings ?? throw new InvalidOperationException("File stack settings are not initialized."),
            _groupNavigationSettings ?? throw new InvalidOperationException("Group navigation settings are not initialized."),
            _managedStorageSettings ?? throw new InvalidOperationException("Managed storage settings are not initialized."),
            _maintenanceSettings ?? throw new InvalidOperationException("Maintenance settings are not initialized."),
            _performanceSettings ?? throw new InvalidOperationException("Performance settings are not initialized."),
            _inProcessFallbackHotkeyActions,
            new InProcessSettingsWidgetActions(
                WidgetManager,
                SettingsService,
                FileService,
                OrganizerService,
                LocalizationService,
                applyQuickCaptureEnabled: enabled =>
                    (_quickCaptureSettings ?? throw new InvalidOperationException(
                        "Quick Capture is not initialized.")).SetEnabledAsync(enabled, reveal: enabled),
                applyTodoEnabled: enabled =>
                    (_todoSettings ?? throw new InvalidOperationException(
                        "Todo settings are not initialized.")).SetEnabledAsync(enabled),
                openCustomWidgetStudio: (widgetId, initialIdea) => ShowCustomWidgetStudio(widgetId, initialIdea)),
            new InProcessSettingsRestoreActions(
                DataBackupService ?? throw new InvalidOperationException("Backup service is not initialized."),
                AttachmentHealthService ?? throw new InvalidOperationException("Attachment health service is not initialized.")),
            new InProcessSettingsAppActions(this, LocalizationService),
            new InProcessSettingsDataActions(
                QuickCaptureService ?? throw new InvalidOperationException("Quick Capture service is not initialized."),
                LocalizationService,
                clipboardServiceProvider: () => _clipboardHistoryService),
            AppUpdateService,
            action => UiDispatcherQueue.TryEnqueue(() => action()));
        _inProcessFallbackSettingsWindow = input.CreateWindow();
        _inProcessFallbackSettingsWindow.Closed += InProcessFallbackSettingsWindow_Closed;
        return _inProcessFallbackSettingsWindow;
    }

    private void InProcessFallbackSettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        if (sender is SettingsWindow settingsWindow)
        {
            settingsWindow.Closed -= InProcessFallbackSettingsWindow_Closed;
        }

        if (ReferenceEquals(_inProcessFallbackSettingsWindow, sender))
        {
            _inProcessFallbackSettingsWindow = null;
        }

        ScheduleLightMemoryCleanup(completedHeavyOperation: true);
        ScheduleBackgroundMemoryCleanup("settings-fallback-closed");
    }

    internal IntPtr TryGetInProcessFallbackSettingsWindowHandle(bool requireVisible)
    {
        if (_inProcessFallbackSettingsWindow is not { } window)
        {
            return IntPtr.Zero;
        }

        if (requireVisible && !window.IsVisibleToUser)
        {
            return IntPtr.Zero;
        }

        return WindowNative.GetWindowHandle(window);
    }

    internal Task StopInProcessFallbackSettingsCloudBackupActionsAsync() =>
        _inProcessFallbackSettingsWindow?.StopCloudBackupActionsAsync() ?? Task.CompletedTask;

    internal void NotifyInProcessFallbackHotkeyRegistrationChanged() =>
        _inProcessFallbackHotkeyActions?.NotifyRegistrationChanged();

    internal void CloseInProcessFallbackSettingsWindowForShutdown()
    {
        _inProcessFallbackSettingsWindow?.CloseForShutdown();
        _inProcessFallbackSettingsWindow = null;
        _inProcessFallbackHotkeyActions = null;
    }

    private IntPtr GetVisibleSettingsWindowHandleCore()
    {
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
        IntPtr smokeHwnd = TryGetInProcessSettingsWindowHandleForSmoke();
        if (smokeHwnd != IntPtr.Zero)
        {
            return smokeHwnd;
        }
#endif
        IntPtr fallbackHwnd = TryGetInProcessFallbackSettingsWindowHandle(requireVisible: true);
        if (fallbackHwnd != IntPtr.Zero)
        {
            return fallbackHwnd;
        }

        if (_settingsHostWindow is { IsVisibleToUser: true } hostWindow)
        {
            return WindowNative.GetWindowHandle(hostWindow);
        }

        return IntPtr.Zero;
    }

    private IntPtr GetAnySettingsHostWindowHandle()
    {
        IntPtr fallbackHwnd = TryGetInProcessFallbackSettingsWindowHandle(requireVisible: false);
        if (fallbackHwnd != IntPtr.Zero)
        {
            return fallbackHwnd;
        }

        if (_settingsHostWindow is { } hostWindow)
        {
            return WindowNative.GetWindowHandle(hostWindow);
        }

        return IntPtr.Zero;
    }

#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
    private bool HasInProcessSettingsWindow => HasInProcessSettingsWindowForSmoke() || HasInProcessFallbackSettingsWindow;
    private async Task StopInProcessSettingsCloudBackupActionsAsync()
    {
        await StopInProcessSettingsCloudBackupActionsForSmokeAsync();
        await StopInProcessFallbackSettingsCloudBackupActionsAsync();
    }
    private void CloseInProcessSettingsWindowForShutdown()
    {
        CloseInProcessSettingsWindowForSmokeShutdown();
        CloseInProcessFallbackSettingsWindowForShutdown();
    }
#else
    private bool HasInProcessSettingsWindow => HasInProcessFallbackSettingsWindow;
    private Task StopInProcessSettingsCloudBackupActionsAsync() => StopInProcessFallbackSettingsCloudBackupActionsAsync();
    private void CloseInProcessSettingsWindowForShutdown() => CloseInProcessFallbackSettingsWindowForShutdown();
#endif

    internal static bool TryForwardSameChannelSecondaryLaunchBeforeXaml(out int exitCode)
    {
        exitCode = 0;
        if (AppDistributionService.Current.IsPackaged)
        {
            return false;
        }

        string[] rawArgs = Environment.GetCommandLineArgs();
        if (StudioHostLaunch.IsStudioHostLaunch(rawArgs))
        {
            return false;
        }

        if (SettingsHostLaunch.IsSettingsHostLaunch(rawArgs) &&
            SettingsHostLaunch.TryGetMainProcessId(rawArgs) > 0)
        {
            return false;
        }

        string[] commandLineArgs = SettingsHostLaunch.IsBareSettingsArgumentWithoutPid(rawArgs)
            ? [.. rawArgs, JumpListService.ArgOpenSettings]
            : rawArgs;

        if (TryGetAwaitParentExitProcessId(commandLineArgs) is not null ||
            NativeNotificationActivationBootstrap.IsNotificationLaunch(commandLineArgs) ||
            IsAutomatedProbeOrSmokeLaunch(commandLineArgs))
        {
            return false;
        }

        DeskBox.Services.Distribution.PrimaryInstanceOwnerInfo? owner =
            DeskBox.Services.Distribution.ChannelCoexistenceService.TryReadPrimaryInstanceOwner(DeskBoxDataPathService.Current, Log);
        if (owner is null || owner.Channel != AppDistributionService.Current.CurrentChannel)
        {
            return false;
        }

        bool primaryMutexHeldByAnotherInstance = false;
        try
        {
            if (!Mutex.TryOpenExisting(DeskBoxDataPathService.Current.SingleInstanceMutexName, out Mutex? probeMutex))
            {
                return false;
            }

            try
            {
                bool acquired;
                try
                {
                    acquired = probeMutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (acquired)
                {
                    try { probeMutex.ReleaseMutex(); } catch { }
                    return false;
                }

                primaryMutexHeldByAnotherInstance = true;
            }
            finally
            {
                probeMutex.Dispose();
            }
        }
        catch (UnauthorizedAccessException)
        {
            // A running higher-integrity primary instance created the mutex
            // with an admin DACL; treat access denied as an active primary.
            primaryMutexHeldByAnotherInstance = true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }

        if (!primaryMutexHeldByAnotherInstance)
        {
            return false;
        }

        if (StartupLaunchPolicy.IsStartupLaunch(commandLineArgs, null, false))
        {
            Log("Another instance running; startup launch exiting silently (pre-XAML)");
            DrainLogQueue();
            return true;
        }

        string? jumpListArg = JumpListService.TryGetJumpListArgument(string.Join(' ', commandLineArgs));
        string activationKind = jumpListArg is not null
            ? $"jump-list:{jumpListArg}"
            : "bare";
        Log(
            $"[Activation] Secondary instance (pre-XAML) kind={activationKind} " +
            $"argumentCount={Math.Max(0, rawArgs.Length - 1)} parent=skipped");

        if (jumpListArg is not null)
        {
            StorePendingJumpListArgument(jumpListArg);
        }

        string? protocolUri = DeskBoxProtocolActivationService.TryExtractProtocolUri(commandLineArgs, null);
        if (!string.IsNullOrWhiteSpace(protocolUri))
        {
            DeskBoxProtocolActivationService.StorePendingProtocolUri(protocolUri);
            Log($"[Protocol] Forwarded deskbox:// URI to running instance uri={protocolUri}");
        }

        string? pendingUpdateResult = TryGetUpdateInstallOutcome(commandLineArgs);
        if (!string.IsNullOrWhiteSpace(pendingUpdateResult))
        {
            StorePendingUpdateInstallResult(pendingUpdateResult);
        }

        Log("Another instance running, signaling existing instance (pre-XAML)");
        try
        {
            using EventWaitHandle? activationEvent = Platform.SingleInstanceAndActivationInterop.CreateOrOpenActivationEvent(
                DeskBoxDataPathService.Current.ActivationEventName,
                Log);
            activationEvent?.Set();
        }
        catch (Exception ex)
        {
            Log($"Failed to signal existing instance: {ex}");
        }

        DrainLogQueue();
        return true;
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
        // WinRT IInitializeWithWindow::Initialize requires an HWND owned by the calling process.
        IntPtr inProcessVisible = GetVisibleSettingsWindowHandle();
        if (IsVisibleNonEmptyProcessWindow(inProcessVisible, (uint)Environment.ProcessId))
        {
            return inProcessVisible;
        }

        IntPtr visibleDeskBoxHwnd = FindVisibleTopLevelWindowForProcess(
            (uint)Environment.ProcessId,
            excludeHwnd: TrayHostWindowHandle);
        if (visibleDeskBoxHwnd != IntPtr.Zero)
        {
            return visibleDeskBoxHwnd;
        }

        IntPtr inProcessSettingsAny = GetAnySettingsHostWindowHandle();
        if (inProcessSettingsAny != IntPtr.Zero &&
            Platform.Win32Helper.IsWindow(inProcessSettingsAny) &&
            Platform.Win32Helper.IsWindowInCurrentProcess(inProcessSettingsAny))
        {
            return inProcessSettingsAny;
        }

        if (TrayHostWindowHandle != IntPtr.Zero &&
            Platform.Win32Helper.IsWindow(TrayHostWindowHandle) &&
            Platform.Win32Helper.IsWindowInCurrentProcess(TrayHostWindowHandle))
        {
            return TrayHostWindowHandle;
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
