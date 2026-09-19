using Microsoft.Win32.SafeHandles;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed class DesktopAutoOrganizationWatcher : IDisposable
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StabilityProbeDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ActivityBurstWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ActivityQuietDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WatcherRecoveryDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan DeferredRetryDelay = TimeSpan.FromMinutes(2);
    private const int WatcherBufferSizeBytes = 64 * 1024;
    private const int StableProbeCount = 4;
    private const int RequiredStableMatches = 3;
    private const int FastRetryAttemptLimit = 8;

    private readonly SettingsService _settingsService;
    private readonly OrganizerService _organizerService;
    private readonly WidgetManager _widgetManager;
    private readonly DesktopOrganizationRuleResolver _ruleResolver = new();
    private readonly DesktopOrganizationScanner _scanner;
    private readonly DesktopAutoOrganizationStateMachine _states = new();
    private readonly DesktopAutoOrganizationBaseline _baseline = new();
    private readonly object _baselineReconcileGate = new();
    private readonly DesktopAutoOrganizationBaselineEventBuffer _baselineEvents = new();
    private readonly DesktopAutoOrganizationActivityTracker _activityTracker;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _retrySignal = new(0, int.MaxValue);
    private readonly FileSystemWatcher _watcher;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private CancellationTokenSource _featureCts = new();
    private Task? _retryPump;
    private bool _lastEnabled;
    private bool _disposed;
    private int _watcherRecoveryScheduled;
    private int _watcherRecoveryAttempts;

    public event Action<DesktopAutoOrganizationCompleted>? ItemOrganized;

    public DesktopAutoOrganizationWatcher(
        SettingsService settingsService,
        OrganizerService organizerService,
        WidgetManager widgetManager,
        Func<string>? desktopPathProvider = null)
        : this(
            settingsService,
            organizerService,
            widgetManager,
            desktopPathProvider,
            () => DateTimeOffset.UtcNow,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    internal DesktopAutoOrganizationWatcher(
        SettingsService settingsService,
        OrganizerService organizerService,
        WidgetManager widgetManager,
        Func<string>? desktopPathProvider,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _settingsService = settingsService;
        _organizerService = organizerService;
        _widgetManager = widgetManager;
        _utcNow = utcNow;
        _delayAsync = delayAsync;
        _activityTracker = new DesktopAutoOrganizationActivityTracker(ActivityBurstWindow);

        string desktopPath = desktopPathProvider?.Invoke() ??
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(desktopPath);
        _scanner = new DesktopOrganizationScanner(
            new DesktopOrganizationClassifier(),
            () => desktopPath);
        _watcher = new FileSystemWatcher(desktopPath)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.DirectoryName |
                           NotifyFilters.Size |
                           NotifyFilters.LastWrite,
            InternalBufferSize = WatcherBufferSizeBytes,
            EnableRaisingEvents = false
        };
        _watcher.Created += OnCreatedOrChanged;
        _watcher.Changed += OnCreatedOrChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnWatcherError;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public void Start()
    {
        ThrowIfDisposed();
        _retryPump ??= RetryPumpAsync(_lifetimeCts.Token);
        _lastEnabled = _settingsService.Settings.DesktopAutoOrganizationEnabled;
        if (!_lastEnabled)
        {
            _featureCts.Cancel();
            _watcher.EnableRaisingEvents = false;
            return;
        }

        BeginEnabledCycle();
        DateTimeOffset cutoff =
            _settingsService.Settings.DesktopAutoOrganizationBaselineUtc ?? _utcNow();
        // Notifications are live before the first enumeration. The second
        // complete capture closes the remaining enumerate/commit race.
        EnableWatcherOrScheduleRecovery();
        bool baselineReady = ReconcileBaselinePair(
            BaselineReconciliationMode.SinceCutoff,
            cutoff);
        if (!baselineReady)
        {
            ScheduleWatcherRecovery();
        }
        _states.ResumeDeferred(_utcNow());
        SignalRetryPump();
    }

    private void OnSettingsChanged()
    {
        bool enabled = _settingsService.Settings.DesktopAutoOrganizationEnabled;
        if (!enabled && _lastEnabled)
        {
            DisableFeature();
            _lastEnabled = false;
            return;
        }

        if (enabled && !_lastEnabled)
        {
            _lastEnabled = true;
            BeginEnabledCycle();
            DateTimeOffset cutoff =
                _settingsService.Settings.DesktopAutoOrganizationBaselineUtc ?? _utcNow();
            EnableWatcherOrScheduleRecovery();
            bool baselineReady = ReconcileBaselinePair(
                BaselineReconciliationMode.SinceCutoff,
                cutoff);
            if (!baselineReady)
            {
                ScheduleWatcherRecovery();
            }
            _states.ResumeDeferred(_utcNow());
            SignalRetryPump();
        }
    }

    private void BeginEnabledCycle()
    {
        _featureCts.Dispose();
        _featureCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        Interlocked.Exchange(ref _watcherRecoveryAttempts, 0);
    }

    private void DisableFeature()
    {
        _watcher.EnableRaisingEvents = false;
        _featureCts.Cancel();
        _states.SuspendRecoverableItems();
        Interlocked.Exchange(ref _watcherRecoveryAttempts, 0);
        SignalRetryPump();
    }

    private void EnableWatcherOrScheduleRecovery()
    {
        try
        {
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopAutoOrganization] Watcher start failed: {ex}");
            ScheduleWatcherRecovery();
        }
    }

    private bool ReconcileBaseline(
        BaselineReconciliationMode mode,
        DateTimeOffset? cutoff = null)
    {
        if (!TryCaptureCompleteDirectory(out HashSet<string>? current, out Exception? error))
        {
            // Never replace a known-good baseline with a partial enumeration.
            App.Log($"[DesktopAutoOrganization] Baseline retained after incomplete enumeration: {error}");
            return false;
        }

        HashSet<string> previous = _baseline.Snapshot();

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mode == BaselineReconciliationMode.SincePreviousBaseline)
        {
            candidates.UnionWith(current.Where(path => !previous.Contains(path)));
        }
        else if (cutoff is not null)
        {
            foreach (string path in current)
            {
                try
                {
                    if (File.Exists(path) &&
                        File.GetCreationTimeUtc(path) > cutoff.Value.UtcDateTime)
                    {
                        candidates.Add(path);
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[DesktopAutoOrganization] Baseline metadata failed for '{path}': {ex}");
                    // Metadata failure means this capture is not complete.
                    return false;
                }
            }
        }

        // A rapid disable/enable can leave an old Processing task unwinding
        // cancellation or safely completing an already-started move. Exclude
        // every non-terminal identity, not only Deferred, so the new baseline
        // cannot permanently suppress its recovery.
        HashSet<string> nonTerminal = new(
            _states.GetNonTerminalPaths(),
            StringComparer.OrdinalIgnoreCase);
        _baseline.TryReplace(
            captureIsComplete: true,
            current,
            pendingPaths: candidates,
            excludedPaths: nonTerminal);

        foreach (string path in candidates)
        {
            Queue(path, bypassBaseline: true);
        }

        return true;
    }

    private bool ReconcileBaselinePair(
        BaselineReconciliationMode mode,
        DateTimeOffset? cutoff = null)
    {
        lock (_baselineReconcileGate)
        {
            BeginBaselineBuild();
            bool firstComplete = false;
            bool secondComplete = false;
            try
            {
                firstComplete = ReconcileBaseline(mode, cutoff);
                secondComplete = ReconcileBaseline(mode, cutoff);
            }
            finally
            {
                FlushBaselineEventBuffer();
            }

            return firstComplete && secondComplete;
        }
    }

    private void BeginBaselineBuild()
    {
        _baselineEvents.Begin();
    }

    private bool TryBufferBaselineDeletion(string path)
    {
        string? fullPath = NormalizePath(path);
        if (fullPath is null)
        {
            return true;
        }

        return _baselineEvents.TryBufferDeletion(fullPath);
    }

    private bool TryBufferBaselineEvent(string path, bool bypassBaseline)
    {
        string? fullPath = NormalizePath(path);
        if (fullPath is null)
        {
            return true;
        }

        return _baselineEvents.TryBufferChange(fullPath, bypassBaseline);
    }

    private void FlushBaselineEventBuffer()
    {
        while (_baselineEvents.TryDrain(out DesktopAutoOrganizationBaselineEventBatch batch))
        {
            foreach (string path in batch.Deleted)
            {
                _states.MarkRenamedOrMissing(path);
                RemoveFromBaseline(path);
            }

            foreach (string path in batch.Forced)
            {
                Queue(path, bypassBaseline: true);
            }

            foreach (string path in batch.Changed.Except(
                         batch.Forced,
                         StringComparer.OrdinalIgnoreCase))
            {
                Queue(path, preserveRetryAttempts: true);
            }
        }
    }

    private bool TryCaptureCompleteDirectory(
        out HashSet<string> paths,
        out Exception? error)
    {
        paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        try
        {
            // GetFileSystemEntries materializes the full enumeration. If any
            // part fails, no partial result is committed.
            foreach (string path in Directory.GetFileSystemEntries(
                         _watcher.Path,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                paths.Add(Path.GetFullPath(path));
            }

            return true;
        }
        catch (Exception ex)
        {
            paths.Clear();
            error = ex;
            return false;
        }
    }

    private void OnCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        ObserveActivity(e.FullPath);
        bool bypassBaseline = e.ChangeType == WatcherChangeTypes.Created;
        if (!TryBufferBaselineEvent(e.FullPath, bypassBaseline))
        {
            Queue(
                e.FullPath,
                bypassBaseline,
                preserveRetryAttempts: !bypassBaseline);
        }
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        ObserveActivity(e.FullPath);
        if (TryBufferBaselineDeletion(e.FullPath))
        {
            return;
        }

        string? path = NormalizePath(e.FullPath);
        if (path is null)
        {
            return;
        }

        _states.MarkRenamedOrMissing(path);
        RemoveFromBaseline(path);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        ObserveActivity(e.OldFullPath);
        ObserveActivity(e.FullPath);
        string? oldPath = NormalizePath(e.OldFullPath);
        if (oldPath is not null && !TryBufferBaselineDeletion(oldPath))
        {
            _states.MarkRenamedOrMissing(oldPath);
            RemoveFromBaseline(oldPath);
        }

        if (!TryBufferBaselineEvent(e.FullPath, bypassBaseline: true))
        {
            Queue(e.FullPath, bypassBaseline: true);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        App.Log($"[DesktopAutoOrganization] File-system watcher fault: {e.GetException().Message}");
        ScheduleWatcherRecovery();
    }

    private void ScheduleWatcherRecovery()
    {
        if (_disposed ||
            !_settingsService.Settings.DesktopAutoOrganizationEnabled ||
            Interlocked.Exchange(ref _watcherRecoveryScheduled, 1) != 0)
        {
            return;
        }

        _ = RecoverWatcherContinuouslyAsync(_featureCts.Token);
    }

    private async Task RecoverWatcherContinuouslyAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_disposed &&
                   !cancellationToken.IsCancellationRequested &&
                   _settingsService.Settings.DesktopAutoOrganizationEnabled)
            {
                int attempt = Interlocked.Increment(ref _watcherRecoveryAttempts);
                TimeSpan delay = TimeSpan.FromMilliseconds(
                    Math.Min(
                        30_000,
                        WatcherRecoveryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
                await _delayAsync(delay, cancellationToken);

                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.EnableRaisingEvents = true;
                    // Compare against the last complete baseline so only files
                    // from the unhealthy window are queued.
                    if (!ReconcileBaselinePair(BaselineReconciliationMode.SincePreviousBaseline))
                    {
                        throw new IOException("Desktop baseline reconciliation remained incomplete.");
                    }
                    Interlocked.Exchange(ref _watcherRecoveryAttempts, 0);
                    App.Log("[DesktopAutoOrganization] Watcher recovered and baseline reconciled.");
                    return;
                }
                catch (Exception ex)
                {
                    App.Log(
                        $"[DesktopAutoOrganization] Watcher recovery attempt {attempt} failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref _watcherRecoveryScheduled, 0);
            if (!_disposed &&
                _settingsService.Settings.DesktopAutoOrganizationEnabled &&
                !_watcher.EnableRaisingEvents)
            {
                ScheduleWatcherRecovery();
            }
        }
    }

    private void Queue(
        string path,
        bool bypassBaseline = false,
        bool preserveRetryAttempts = false)
    {
        if (_disposed || !_settingsService.Settings.DesktopAutoOrganizationEnabled)
        {
            return;
        }

        string? fullPath = NormalizePath(path);
        if (fullPath is null)
        {
            return;
        }

        if (!bypassBaseline && _baseline.Contains(fullPath))
        {
            return;
        }

        StartProcessing(_states.BeginPending(fullPath, preserveRetryAttempts));
    }

    private void StartProcessing(DesktopAutoOrganizationWorkItem workItem)
    {
        if (!_states.TryTransition(
                workItem,
                DesktopAutoOrganizationItemState.Pending,
                DesktopAutoOrganizationItemState.Settling))
        {
            return;
        }

        CancellationToken featureToken = _featureCts.Token;
        _ = ProcessAfterSettleAsync(workItem, featureToken);
    }

    private async Task ProcessAfterSettleAsync(
        DesktopAutoOrganizationWorkItem workItem,
        CancellationToken cancellationToken)
    {
        bool moveSucceeded = false;
        try
        {
            await _delayAsync(SettleDelay, cancellationToken);
            await WaitForDirectoryQuietAsync(workItem.Path, cancellationToken);
            if (_disposed ||
                !_settingsService.Settings.DesktopAutoOrganizationEnabled ||
                !_states.TryTransition(
                    workItem,
                    DesktopAutoOrganizationItemState.Settling,
                    DesktopAutoOrganizationItemState.Processing))
            {
                return;
            }

            DesktopOrganizationFileSnapshot item =
                _scanner.CreateAutoOrganizationSnapshot(workItem.Path);
            if (!item.IsEligible)
            {
                DesktopAutoOrganizationItemState excludedState =
                    DesktopAutoOrganizationStatePolicy.ForSnapshotExclusion(
                        item.ExclusionReason,
                        File.Exists(workItem.Path));
                if (excludedState == DesktopAutoOrganizationItemState.Deferred)
                {
                    Defer(workItem, DesktopAutoOrganizationRetryKind.Finite);
                }
                else if (excludedState == DesktopAutoOrganizationItemState.Missing)
                {
                    MarkMissing(workItem);
                }
                else
                {
                    MarkIgnored(workItem);
                }

                return;
            }

            StableFileProbeResult stable = await WaitForStableFileAsync(
                workItem.Path,
                cancellationToken);
            if (stable.Status == StableFileStatus.Missing)
            {
                MarkMissing(workItem);
                return;
            }

            if (stable.Status == StableFileStatus.Deferred)
            {
                Defer(workItem, DesktopAutoOrganizationRetryKind.Persistent);
                return;
            }

            // The first snapshot may have been captured while a file was
            // still growing. Re-check the per-file safety limit after the
            // stability probe so a file that crossed 100 MB during settling
            // cannot slip through the quick-organization path.
            if (stable.Size > DesktopOrganizationScanner.SlowItemThresholdBytes)
            {
                MarkIgnored(workItem);
                return;
            }

            item = item with
            {
                Size = stable.Size,
                LastWriteTimeUtc = stable.LastWriteTimeUtc
            };
            if (_organizerService.AutoOrganizationSuppressions.TryConsume(
                    workItem.Path))
            {
                App.Log(
                    $"[DesktopAutoOrganization] Explicit restore retained on desktop: " +
                    $"'{workItem.Path}'.");
                MarkIgnored(workItem);
                return;
            }

            DesktopOrganizationRule? rule = _ruleResolver.Resolve(
                item,
                _settingsService.Settings.DesktopOrganizationRules,
                _settingsService.Settings.Widgets);
            if (rule is null)
            {
                MarkIgnored(workItem);
                return;
            }

            WidgetConfig? target = _settingsService.Settings.Widgets.FirstOrDefault(widget =>
                string.Equals(widget.Id, rule.TargetWidgetId, StringComparison.Ordinal));
            if (target is null)
            {
                MarkIgnored(workItem);
                return;
            }

            await DesktopOrganizationTransaction.OperationGate.WaitAsync(cancellationToken);
            try
            {
                if (!_states.IsCurrent(
                        workItem,
                        DesktopAutoOrganizationItemState.Processing))
                {
                    return;
                }

                if (!File.Exists(workItem.Path))
                {
                    MarkMissing(workItem);
                    return;
                }

                if (!TryCaptureFileFingerprint(
                        workItem.Path,
                        out StableFileFingerprint currentFingerprint))
                {
                    if (File.Exists(workItem.Path))
                    {
                        Defer(workItem, DesktopAutoOrganizationRetryKind.Persistent);
                    }
                    else
                    {
                        MarkMissing(workItem);
                    }

                    return;
                }

                if (stable.Fingerprint != currentFingerprint)
                {
                    Queue(workItem.Path, bypassBaseline: true);
                    return;
                }

                // Recheck after filesystem IO and immediately before starting
                // the path-based move. A same-path replacement has a new generation.
                if (!_states.IsCurrent(
                        workItem,
                        DesktopAutoOrganizationItemState.Processing))
                {
                    return;
                }

                OrganizerOperationResult operation = await _organizerService.OrganizeDropAsync(
                    target,
                    target.Name,
                    [workItem.Path],
                    move: true,
                    useShellProgress: false);
                moveSucceeded = true;

                // Moving the source is the transaction boundary. Mark it before
                // best-effort UI work so refresh/notification failures never retry it.
                _states.MarkTerminal(
                    workItem,
                    DesktopAutoOrganizationItemState.Completed);
                RemoveFromBaseline(workItem.Path);

                try
                {
                    await _widgetManager.RefreshFileWidgetAsync(target.Id);
                }
                catch (Exception ex)
                {
                    App.Log($"[DesktopAutoOrganization] Refresh failed after moving '{workItem.Path}': {ex}");
                }

                try
                {
                    ItemOrganized?.Invoke(new DesktopAutoOrganizationCompleted(
                        operation.History.Id,
                        Path.GetFileName(workItem.Path),
                        target.Id,
                        target.Name));
                }
                catch (Exception ex)
                {
                    App.Log($"[DesktopAutoOrganization] Notification failed after moving '{workItem.Path}': {ex}");
                }
            }
            finally
            {
                DesktopOrganizationTransaction.OperationGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Settle-phase work was already preserved by DisableFeature. A
            // cancellation after entering Processing but before the actual move
            // must also become recoverable instead of sticking in Processing.
            if (!moveSucceeded && File.Exists(workItem.Path))
            {
                Defer(workItem, DesktopAutoOrganizationRetryKind.Persistent);
            }
            else if (!moveSucceeded)
            {
                MarkMissing(workItem);
            }
        }
        catch (IOException ex) when (!moveSucceeded)
        {
            // A mapped target can temporarily disappear (network drive,
            // removable disk, or a transient provider error). Keep the new
            // desktop item recoverable instead of consuming the finite
            // metadata retry budget and silently turning it into Ignored.
            App.Log($"[DesktopAutoOrganization] Storage unavailable for '{workItem.Path}': {ex}");
            if (File.Exists(workItem.Path))
            {
                Defer(workItem, DesktopAutoOrganizationRetryKind.Persistent);
            }
            else
            {
                MarkMissing(workItem);
            }
        }
        catch (UnauthorizedAccessException ex) when (!moveSucceeded)
        {
            // Permission changes on the source or mapped target are also
            // recoverable and should be retried after access is restored.
            App.Log($"[DesktopAutoOrganization] Access unavailable for '{workItem.Path}': {ex}");
            if (File.Exists(workItem.Path))
            {
                Defer(workItem, DesktopAutoOrganizationRetryKind.Persistent);
            }
            else
            {
                MarkMissing(workItem);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopAutoOrganization] Failed path='{workItem.Path}': {ex}");
            if (moveSucceeded)
            {
                _states.MarkTerminal(
                    workItem,
                    DesktopAutoOrganizationItemState.Completed);
                RemoveFromBaseline(workItem.Path);
            }
            else if (!File.Exists(workItem.Path))
            {
                MarkMissing(workItem);
            }
            else
            {
                Defer(workItem, DesktopAutoOrganizationRetryKind.Finite);
            }
        }
    }

    private async Task<StableFileProbeResult> WaitForStableFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        StableFileProbeResult? previous = null;
        int stableMatches = 0;
        for (int probe = 0; probe < StableProbeCount; probe++)
        {
            if (!File.Exists(path) || Directory.Exists(path))
            {
                return StableFileProbeResult.Missing;
            }

            try
            {
                if (!TryCaptureFileFingerprint(path, out StableFileFingerprint fingerprint))
                {
                    previous = null;
                    await _delayAsync(StabilityProbeDelay, cancellationToken);
                    continue;
                }

                var current = new StableFileProbeResult(
                    StableFileStatus.Stable,
                    fingerprint);
                if (previous == current)
                {
                    stableMatches++;
                    if (stableMatches >= RequiredStableMatches)
                    {
                        return current;
                    }
                }
                else
                {
                    stableMatches = 0;
                }

                previous = current;
            }
            catch (IOException)
            {
                previous = null;
            }
            catch (UnauthorizedAccessException)
            {
                previous = null;
            }

            await _delayAsync(StabilityProbeDelay, cancellationToken);
        }

        return File.Exists(path)
            ? StableFileProbeResult.Deferred
            : StableFileProbeResult.Missing;
    }

    private async Task WaitForDirectoryQuietAsync(
        string path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            DateTimeOffset now = _utcNow();
            DesktopDirectoryActivitySnapshot activity =
                _activityTracker.GetSnapshot(path, now);
            if (activity.EventCount < 2)
            {
                return;
            }

            TimeSpan quietFor = now - activity.LastEventAt;
            if (quietFor >= ActivityQuietDelay)
            {
                return;
            }

            TimeSpan remaining = ActivityQuietDelay - quietFor;
            await _delayAsync(
                remaining > TimeSpan.FromMilliseconds(250)
                    ? TimeSpan.FromMilliseconds(250)
                    : remaining,
                cancellationToken);
        }
    }

    private void ObserveActivity(string path)
    {
        _activityTracker.Observe(path, _utcNow());
    }

    private static bool TryCaptureFileFingerprint(
        string path,
        out StableFileFingerprint fingerprint)
    {
        fingerprint = default;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                bufferSize: 1,
                FileOptions.SequentialScan);
            var file = new FileInfo(path);
            fingerprint = new StableFileFingerprint(
                file.Length,
                file.LastWriteTimeUtc,
                file.CreationTimeUtc,
                TryGetFileIdentity(stream.SafeFileHandle));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static FileIdentity? TryGetFileIdentity(SafeFileHandle handle)
    {
        return Kernel32NativeMethods.GetFileInformationByHandle(
            handle,
            out Kernel32NativeMethods.ByHandleFileInformation information)
            ? new FileIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow)
            : null;
    }

    private void MarkIgnored(DesktopAutoOrganizationWorkItem workItem)
    {
        if (_states.MarkTerminal(workItem, DesktopAutoOrganizationItemState.Ignored))
        {
            _baseline.Add(workItem.Path);
        }
    }

    private void MarkMissing(DesktopAutoOrganizationWorkItem workItem)
    {
        if (_states.MarkTerminal(workItem, DesktopAutoOrganizationItemState.Missing))
        {
            RemoveFromBaseline(workItem.Path);
        }
    }

    private void Defer(
        DesktopAutoOrganizationWorkItem workItem,
        DesktopAutoOrganizationRetryKind retryKind)
    {
        DesktopAutoOrganizationStateSnapshot? snapshot = _states.GetSnapshot(workItem.Path);
        int nextAttempt = (snapshot?.RetryAttempts ?? 0) + 1;
        DesktopAutoOrganizationRetryDecision decision =
            DesktopAutoOrganizationRetrySchedule.Evaluate(
                retryKind,
                nextAttempt,
                FastRetryAttemptLimit,
                DeferredRetryDelay);
        if (!decision.ShouldRetry)
        {
            App.Log(
                $"[DesktopAutoOrganization] Ignoring '{workItem.Path}' after " +
                $"{FastRetryAttemptLimit} transient metadata/operation retries.");
            MarkIgnored(workItem);
            return;
        }

        if (_states.MarkDeferred(workItem, _utcNow().Add(decision.Delay)))
        {
            SignalRetryPump();
        }
    }

    private async Task RetryPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_settingsService.Settings.DesktopAutoOrganizationEnabled)
                {
                    await _retrySignal.WaitAsync(cancellationToken);
                    continue;
                }

                DateTimeOffset now = _utcNow();
                IReadOnlyList<DesktopAutoOrganizationWorkItem> due =
                    _states.TakeDueDeferred(now);
                foreach (DesktopAutoOrganizationWorkItem workItem in due)
                {
                    StartProcessing(workItem);
                }

                DateTimeOffset? nextRetryAt = _states.GetNextRetryAt();
                if (nextRetryAt is null)
                {
                    await _retrySignal.WaitAsync(cancellationToken);
                    continue;
                }

                TimeSpan delay = nextRetryAt.Value - _utcNow();
                if (delay <= TimeSpan.Zero)
                {
                    continue;
                }

                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task delayTask = _delayAsync(delay, waitCts.Token);
                Task signalTask = _retrySignal.WaitAsync(waitCts.Token);
                Task completed = await Task.WhenAny(delayTask, signalTask);
                await completed;
                waitCts.Cancel();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void SignalRetryPump()
    {
        try
        {
            _retrySignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void RemoveFromBaseline(string path)
    {
        _baseline.Remove(path);
    }

    private static string? NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _featureCts.Cancel();
        _lifetimeCts.Cancel();
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnCreatedOrChanged;
        _watcher.Changed -= OnCreatedOrChanged;
        _watcher.Deleted -= OnDeleted;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();
        // RetryPump may still be unwinding its canceled semaphore wait. The
        // managed semaphore is intentionally left for GC to avoid dispose races.
        _featureCts.Dispose();
        _lifetimeCts.Dispose();
    }

    private enum BaselineReconciliationMode
    {
        SinceCutoff,
        SincePreviousBaseline
    }

    private enum StableFileStatus
    {
        Stable,
        Deferred,
        Missing
    }

    private readonly record struct StableFileProbeResult(
        StableFileStatus Status,
        StableFileFingerprint Fingerprint)
    {
        public long Size => Fingerprint.Size;

        public DateTime LastWriteTimeUtc => Fingerprint.LastWriteTimeUtc;

        public static StableFileProbeResult Deferred =>
            new(StableFileStatus.Deferred, default);

        public static StableFileProbeResult Missing =>
            new(StableFileStatus.Missing, default);
    }

    private readonly record struct StableFileFingerprint(
        long Size,
        DateTime LastWriteTimeUtc,
        DateTime CreationTimeUtc,
        FileIdentity? Identity);

    private readonly record struct FileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex);

}

public sealed record DesktopAutoOrganizationCompleted(
    string HistoryId,
    string FileName,
    string TargetWidgetId,
    string TargetWidgetName);
