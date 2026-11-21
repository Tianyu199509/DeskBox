using System.Diagnostics;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Studio;

namespace DeskBox;

/// <summary>
/// Main-process coordinator for the out-of-process Custom Widget Studio companion host
/// (<c>DeskBox.exe --studio &lt;mainPid&gt;</c>), plus bounded retry scheduling for
/// heavy-operation deep memory cleanup.
/// </summary>
public partial class App
{
    private Process? _studioHostProcess;
    private Process? _exitingStudioHost;
    private DateTime? _studioHostStartTime;
    private DateTime? _exitingStudioHostStartTime;
    private bool _studioHostBootConfirmed;
    private int _studioHostGeneration;
    private long _studioHostWindowHandle;
    private long _lastStudioHostStateSequence;
    private readonly object _studioHostStateGate = new();
    private volatile string? _studioHostTargetWidgetId;

    private readonly object _pendingStudioHostIntentsGate = new();
    private StudioShowIntentPayload? _studioHostInitialShowIntent;
    private StudioShowPreviewIntentPayload? _studioHostInitialPreviewIntent;
    private readonly Queue<(string Method, string? Payload)> _pendingStudioHostIntents = new();

    private static int s_pendingHeavyOperationDeepCleanup;
    private static int s_heavyOperationDeepCleanupRetryCount;
    private static int s_heavyOperationDeepCleanupRetryGeneration;
    private static readonly int[] s_heavyOperationDeepCleanupRetryDelaysSeconds = [3, 6, 12, 20, 35, 60];

    internal bool IsStudioHostRunning =>
        _studioHostProcess is { } host && !host.HasExited;

    internal bool IsStudioHostVisible => IsStudioHostRunning;

    internal static void SubscribeWindowClosedOnce(Microsoft.UI.Xaml.Window window, Action onClosed)
    {
        Windows.Foundation.TypedEventHandler<object, Microsoft.UI.Xaml.WindowEventArgs>? handler = null;
        handler = (_, _) =>
        {
            window.Closed -= handler;
            onClosed();
        };
        window.Closed += handler;
    }

    internal (uint Pid, DateTime? StartTime) StudioHostPipeClientIdentity =>
        _studioHostProcess is { } host
            ? ((uint)host.Id, _studioHostStartTime)
            : _exitingStudioHost is { } dying
                ? ((uint)dying.Id, _exitingStudioHostStartTime)
                : (0, null);

    internal bool BelongsToStudioHostProcess(uint processId)
    {
        if (processId == 0)
        {
            return false;
        }

        try
        {
            if (_studioHostProcess is { } host && (uint)host.Id == processId)
            {
                return true;
            }

            if (_exitingStudioHost is { } dying && (uint)dying.Id == processId)
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
        }

        return false;
    }

    internal IntPtr GetStudioHostWindowHandle()
    {
        if (!IsStudioHostRunning)
        {
            return IntPtr.Zero;
        }

        long raw = Interlocked.Read(ref _studioHostWindowHandle);
        if (raw == 0)
        {
            return IntPtr.Zero;
        }

        var hwnd = new IntPtr(raw);
        return Platform.Win32Helper.IsWindow(hwnd) ? hwnd : IntPtr.Zero;
    }

    internal void UpdateStudioHostWindowState(long windowHandle, string? targetWidgetId) =>
        UpdateStudioHostWindowState(windowHandle, targetWidgetId, stateSequence: 0);

    internal void UpdateStudioHostWindowState(long windowHandle, string? targetWidgetId, long stateSequence)
    {
        lock (_studioHostStateGate)
        {
            if (stateSequence > 0)
            {
                if (stateSequence < _lastStudioHostStateSequence)
                {
                    return;
                }

                _lastStudioHostStateSequence = stateSequence;
            }

            Interlocked.Exchange(ref _studioHostWindowHandle, windowHandle);
            _studioHostTargetWidgetId = string.IsNullOrWhiteSpace(targetWidgetId) ? null : targetWidgetId;
        }
    }

    private void ClearStudioHostWindowState()
    {
        long prevHwnd;
        lock (_studioHostStateGate)
        {
            _lastStudioHostStateSequence = 0;
            prevHwnd = Interlocked.Exchange(ref _studioHostWindowHandle, 0);
            _studioHostTargetWidgetId = null;
        }

        if (prevHwnd != 0)
        {
            WidgetManager?.ReleaseRaisedBandGuest(new IntPtr(prevHwnd), "studio-host-exited");
        }
    }

    private void ShowStudioViaHost(
        string? widgetId,
        string? initialIdea,
        bool autoStartGeneration)
    {
        if (IsShuttingDown)
        {
            return;
        }

        if (!DeskBox.Platform.UiDispatch.HasAccess)
        {
            DeskBox.Platform.UiDispatch.RunOrDefer(
                () => ShowStudioViaHost(widgetId, initialIdea, autoStartGeneration));
            return;
        }

        var showIntent = new StudioShowIntentPayload(widgetId, initialIdea, autoStartGeneration);
        if (!IsStudioHostRunning)
        {
            StartStudioHostProcess(initialShow: showIntent, initialPreview: null);
            return;
        }

        string serialized = SettingsIpcPayload.Encode(
            showIntent,
            SettingsIpcJsonContext.Default.StudioShowIntentPayload);
        lock (_pendingStudioHostIntentsGate)
        {
            if (!_studioHostBootConfirmed)
            {
                EnqueuePendingStudioIntentLocked(StudioIpcMethods.HostShow, serialized);
                return;
            }
        }

        TryGrantSettingsHostForegroundRight(_studioHostProcess);
        _ = PushStudioIpcEventAsync(StudioIpcMethods.HostShow, serialized);
    }

    private void ShowStudioPreviewViaHost(
        string manifestJson,
        string? sourceLabel,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales,
        string? originKind)
    {
        if (IsShuttingDown || string.IsNullOrWhiteSpace(manifestJson))
        {
            return;
        }

        if (!DeskBox.Platform.UiDispatch.HasAccess)
        {
            DeskBox.Platform.UiDispatch.RunOrDefer(
                () => ShowStudioPreviewViaHost(manifestJson, sourceLabel, logicJs, locales, originKind));
            return;
        }

        var previewIntent = new StudioShowPreviewIntentPayload(
            ManifestJson: manifestJson,
            SourceLabel: sourceLabel,
            LogicJs: logicJs,
            LocalesJson: StudioLocaleWireHelper.Serialize(locales),
            HasLocales: locales is { HasAnyLocale: true },
            OriginKind: originKind);

        if (!IsStudioHostRunning)
        {
            StartStudioHostProcess(initialShow: null, initialPreview: previewIntent);
            return;
        }

        string serialized = SettingsIpcPayload.Encode(
            previewIntent,
            SettingsIpcJsonContext.Default.StudioShowPreviewIntentPayload);
        lock (_pendingStudioHostIntentsGate)
        {
            if (!_studioHostBootConfirmed)
            {
                EnqueuePendingStudioIntentLocked(StudioIpcMethods.HostShowPreview, serialized);
                return;
            }
        }

        TryGrantSettingsHostForegroundRight(_studioHostProcess);
        _ = PushStudioIpcEventAsync(StudioIpcMethods.HostShowPreview, serialized);
    }

    private void EnqueuePendingStudioIntentLocked(string method, string? payload)
    {
        if (_pendingStudioHostIntents.Count >= 32)
        {
            _ = _pendingStudioHostIntents.Dequeue();
            Log("[StudioHost] Boot-intent queue full; dropped oldest intent");
        }

        _pendingStudioHostIntents.Enqueue((method, payload));
        Log($"[StudioHost] Host still booting; queued intent ({_pendingStudioHostIntents.Count} pending)");
    }

    internal (StudioShowIntentPayload? Show, StudioShowPreviewIntentPayload? Preview) ConsumeInitialStudioBootstrapIntents()
    {
        lock (_pendingStudioHostIntentsGate)
        {
            StudioShowIntentPayload? show = _studioHostInitialShowIntent;
            StudioShowPreviewIntentPayload? preview = _studioHostInitialPreviewIntent;
            _studioHostInitialShowIntent = null;
            _studioHostInitialPreviewIntent = null;
            return (show, preview);
        }
    }

    private void OnStudioHostBootReady()
    {
        List<(string Method, string? Payload)>? toFlush = null;
        lock (_pendingStudioHostIntentsGate)
        {
            _studioHostBootConfirmed = true;
            if (_pendingStudioHostIntents.Count > 0)
            {
                toFlush = [.. _pendingStudioHostIntents];
                _pendingStudioHostIntents.Clear();
            }
        }

        if (toFlush is { Count: > 0 })
        {
            TryGrantSettingsHostForegroundRight(_studioHostProcess);
            foreach ((string method, string? payload) in toFlush)
            {
                _ = PushStudioIpcEventAsync(method, payload);
            }

            Log($"[StudioHost] Boot confirmed; flushed {toFlush.Count} queued intent(s)");
        }
    }

    private void StartStudioHostProcess(
        StudioShowIntentPayload? initialShow,
        StudioShowPreviewIntentPayload? initialPreview)
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
            startInfo.ArgumentList.Add(StudioHostLaunchArgument);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

            Process? previousHost = Interlocked.Exchange(ref _studioHostProcess, null);
            if (previousHost is not null && !ReferenceEquals(previousHost, _exitingStudioHost))
            {
                try { previousHost.Dispose(); } catch { }
            }

            if (_exitingStudioHost is { } staleExiting)
            {
                bool hasExited = false;
                try { hasExited = staleExiting.HasExited; } catch { hasExited = true; }
                if (hasExited &&
                    ReferenceEquals(Interlocked.CompareExchange(ref _exitingStudioHost, null, staleExiting), staleExiting))
                {
                    try { staleExiting.Dispose(); } catch { }
                }
            }

            ClearStudioHostWindowState();
            lock (_pendingStudioHostIntentsGate)
            {
                _studioHostBootConfirmed = false;
                _studioHostInitialShowIntent = initialShow;
                _studioHostInitialPreviewIntent = initialPreview;
                _pendingStudioHostIntents.Clear();
            }

            Process? spawnedHost = Process.Start(startInfo);
            Log("[StudioHost] Spawned pid=" + (spawnedHost?.Id.ToString() ?? "null"));
            if (spawnedHost is not null)
            {
                _studioHostStartTime = TryGetStartTime(spawnedHost);
                Volatile.Write(ref _studioHostProcess, spawnedHost);
                TryGrantSettingsHostForegroundRight(spawnedHost);
                int generation = Interlocked.Increment(ref _studioHostGeneration);
                spawnedHost.EnableRaisingEvents = true;
                void ExitHandler(object? sender, EventArgs args)
                {
                    if (sender is Process host)
                    {
                        host.Exited -= ExitHandler;
                        OnStudioHostProcessExited(generation);
                        if (ReferenceEquals(Interlocked.CompareExchange(ref _studioHostProcess, null, host), host) ||
                            ReferenceEquals(Interlocked.CompareExchange(ref _exitingStudioHost, null, host), host))
                        {
                            try { host.Dispose(); } catch { }
                        }
                    }
                    else
                    {
                        OnStudioHostProcessExited(generation);
                    }
                }

                spawnedHost.Exited += ExitHandler;
            }
        }
        catch (Exception ex)
        {
            Log($"[StudioHost] Spawn failed: {ex.Message}");
        }
    }

    private void OnStudioHostProcessExited(int generation)
    {
        try
        {
            if (generation != Volatile.Read(ref _studioHostGeneration))
            {
                Log("[StudioHost] Exit of superseded host era ignored");
                return;
            }

            ClearStudioHostWindowState();
            FailAllPendingStudioContextQueries();
            NotifyStudioHostClosedForMemoryCleanup();
        }
        catch (Exception ex)
        {
            Log($"[StudioHost] Exit guard failed: {ex.Message}");
        }
    }

    internal void MarkStudioHostExiting(uint senderPid)
    {
        try
        {
            Process? current = _studioHostProcess;
            if (current is not null && (uint)current.Id == senderPid)
            {
                Process? exchanged = Interlocked.CompareExchange(
                    ref _studioHostProcess, null, current);
                if (ReferenceEquals(exchanged, current))
                {
                    _exitingStudioHost = current;
                    _exitingStudioHostStartTime = TryGetStartTime(current);
                    ClearStudioHostWindowState();
                    FailAllPendingStudioContextQueries();
                    Log("[StudioHost] Farewell received; treating host as exited for show decisions");
                    NotifyStudioHostClosedForMemoryCleanup();
                    return;
                }

                Log("[StudioHost] Farewell raced a replacement spawn; new host stays registered");
                return;
            }

            if (_exitingStudioHost is { } dying && (uint)dying.Id == senderPid)
            {
                return;
            }

            Log($"[StudioHost] Stale farewell from pid {senderPid} ignored");
        }
        catch (Exception ex)
        {
            Log($"[StudioHost] Farewell handling failed: {ex.Message}");
        }
    }

    private void NotifyStudioHostClosedForMemoryCleanup()
    {
        if (IsShuttingDown)
        {
            return;
        }

        UiDispatcherQueue?.TryEnqueue(() =>
        {
            if (IsShuttingDown || IsStudioHostVisible)
            {
                return;
            }

            CustomWidgetCapabilityRegistry.ReleaseIdleCache();
            NotifyMemoryCleanupActivity();
            if (WidgetManager?.CaptureMemoryCleanupVisibilitySnapshot().HasNativeVisibleWidgets == false)
            {
                ScheduleBackgroundMemoryCleanup("studio-host-closed");
            }
        });
    }

    private void ShutdownStudioHost()
    {
        Process? host = _studioHostProcess ?? _exitingStudioHost;
        if (host is null)
        {
            return;
        }

        bool alreadyExited = false;
        try { alreadyExited = host.HasExited; } catch { alreadyExited = true; }
        if (!alreadyExited)
        {
            _ = PushStudioIpcEventAsync(StudioIpcMethods.HostShutdown, null);
            try
            {
                if (!host.WaitForExit(3000))
                {
                    host.Kill();
                    host.WaitForExit(1500);
                }
            }
            catch (Exception ex)
            {
                Log($"[StudioHost] Shutdown wait failed: {ex.Message}");
            }
        }

        ClearStudioHostWindowState();
        FailAllPendingStudioContextQueries();
        Process? p1 = Interlocked.Exchange(ref _studioHostProcess, null);
        Process? p2 = Interlocked.Exchange(ref _exitingStudioHost, null);
        try { p1?.Dispose(); } catch { }
        if (!ReferenceEquals(p2, p1))
        {
            try { p2?.Dispose(); } catch { }
        }
    }

    // ── Sticky heavy-operation deep-cleanup retry helper ────────────────

    internal static bool HasPendingHeavyOperationDeepCleanup =>
        Volatile.Read(ref s_pendingHeavyOperationDeepCleanup) != 0;

    internal static void MarkPendingHeavyOperationDeepCleanup()
    {
        Volatile.Write(ref s_pendingHeavyOperationDeepCleanup, 1);
        Volatile.Write(ref s_heavyOperationDeepCleanupRetryCount, 0);
        Interlocked.Increment(ref s_heavyOperationDeepCleanupRetryGeneration);
    }

    internal static void ClearPendingHeavyOperationDeepCleanup()
    {
        Volatile.Write(ref s_pendingHeavyOperationDeepCleanup, 0);
        Volatile.Write(ref s_heavyOperationDeepCleanupRetryCount, 0);
        Interlocked.Increment(ref s_heavyOperationDeepCleanupRetryGeneration);
    }

    private void ScheduleHeavyOperationDeepMemoryCleanupRetry(
        string reason,
        int? requiredBackgroundGeneration,
        string detail,
        bool isCooldownDeferral = false)
    {
        if (IsShuttingDown || !HasPendingHeavyOperationDeepCleanup)
        {
            return;
        }

        int attempt = Interlocked.Increment(ref s_heavyOperationDeepCleanupRetryCount);
        if (attempt > s_heavyOperationDeepCleanupRetryDelaysSeconds.Length)
        {
            ClearPendingHeavyOperationDeepCleanup();
            LogVerbose($"[Memory] Heavy-operation deep cleanup retry budget exhausted reason={reason} lastDetail={detail}");
            return;
        }

        int delaySeconds = s_heavyOperationDeepCleanupRetryDelaysSeconds[attempt - 1];
        if (isCooldownDeferral && delaySeconds < 15)
        {
            delaySeconds = 15;
        }

        int retryGen = Interlocked.Increment(ref s_heavyOperationDeepCleanupRetryGeneration);
        var dispatcherQueue = UiDispatcherQueue;
        dispatcherQueue?.TryEnqueue(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                if (IsShuttingDown ||
                    !HasPendingHeavyOperationDeepCleanup ||
                    retryGen != Volatile.Read(ref s_heavyOperationDeepCleanupRetryGeneration))
                {
                    return;
                }

                await RunHeavyOperationDeepMemoryCleanupAsync(
                    Volatile.Read(ref s_lightMemoryCleanupGeneration),
                    reason: $"{reason}:retry{attempt}",
                    requiredBackgroundGeneration: requiredBackgroundGeneration);
            }
            catch (Exception ex)
            {
                LogVerbose($"[Memory] Heavy-operation deep cleanup retry failed: {ex.Message}");
            }
        });
    }
}
