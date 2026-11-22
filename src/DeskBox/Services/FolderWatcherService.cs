using DeskBox.Platform;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

public sealed record FolderChange(string FullPath, WatcherChangeTypes ChangeType, string? OldFullPath = null);

public sealed record FolderChangeBatch(
    string WatchedPath,
    IReadOnlyList<FolderChange> Changes,
    bool RequiresFullReload,
    int Generation = 0);

public enum FolderWatcherHealth
{
    Stopped,
    Watching,
    Degraded,
    Unavailable,
    AccessDenied
}

public sealed record FolderWatcherHealthSnapshot(
    string? WatchedPath,
    FolderWatcherHealth Status,
    bool NativeWatcherActive,
    bool QueryWatcherActive,
    bool ReconnectPending,
    int ReconnectCount,
    DateTimeOffset? LastEventAt,
    string? LastError);

/// <summary>
/// Watches a folder for file system changes and notifies via events.
/// Uses a shallow <see cref="FileSystemWatcher"/> for low-latency file and directory
/// notifications paired with the shared <see cref="ShellChangeNotificationService"/>
/// (<c>SHChangeNotifyRegister</c>) for Explorer operations and child folder icon updates.
/// Avoids storage-query broker threads and recursive subtree watchers.
/// Implements debouncing using a DispatcherQueueTimer to avoid creating
/// short-lived thread-pool tasks on every file-system event.
/// </summary>
public sealed class FolderWatcherService : IDisposable
{
    private const int DebounceDelayMs = 250;
    internal const int MaxDebounceWaitMs = 1500;
    // Overflow protection, and since the view model stopped escalating large
    // known batches to full reloads, the ONLY count-based escalation left:
    // exceeding the buffered-change cap means events arrive faster than
    // debatching drains them - the same shape as a native buffer overflow -
    // so the batch falls back to the authoritative full reload rather than
    // trust a change list that large.
    internal const int MaxBufferedChangesBeforeReload = 64;
    private const int MaxReconnectAttempts = 8;
    private const int ReconnectBaseDelaySeconds = 2;
    // A persistent per-subtree AccessDenied makes the native watcher error,
    // restart, and error again as fast as events allow. Rate-limiting the
    // restarts and plateauing the reconnect backoff keeps the recovery loop
    // (an ACL fix must self-heal) without flooding the log.
    private const int ReconnectPlateauSeconds = 180;
    private const int LegacyWatcherRestartMinIntervalSeconds = 30;
    internal const int NativeBufferSizeBytes = 32 * 1024;
    internal const NotifyFilters NativeNotifyFilter =
        NotifyFilters.FileName |
        NotifyFilters.DirectoryName |
        NotifyFilters.LastWrite |
        NotifyFilters.Size |
        NotifyFilters.CreationTime |
        NotifyFilters.Attributes;

    private FileSystemWatcher? _legacyWatcher;
    private IDisposable? _shellNotifySubscription;
    private readonly DispatcherQueueTimer _debounceTimer;
    private readonly DispatcherQueueTimer _iconDebounceTimer;
    private readonly DispatcherQueueTimer _reconnectTimer;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly object _lock = new();
    private readonly List<FolderChange> _pendingChanges = [];
    private readonly HashSet<string> _pendingIconPaths = new(StringComparer.OrdinalIgnoreCase);
    private int _pendingGeneration;
    private bool _requiresFullReload;
    private long _firstPendingChangeTick;
    private int _debounceRestartQueued;
    private int _iconDebounceStartQueued;
    private bool _legacyRestartQueued;
    private DateTimeOffset _lastLegacyRestartAtUtc = DateTimeOffset.MinValue;
    private bool _legacyErrorAnnounced;
    private int _watchGeneration;
    private string? _reconnectPath;
    private string? _requestedPath;
    private int _reconnectAttempt;
    private int _reconnectCount;
    private bool _isDisposed;
    private DateTimeOffset? _lastEventAt;
    private FolderWatcherHealth _health = FolderWatcherHealth.Stopped;
    private string? _lastError;

    /// <summary>
    /// Fired when the watched folder's contents change (debounced).
    /// Always raised on the UI thread.
    /// </summary>
    public event Action<FolderChangeBatch>? FolderChanged;

    /// <summary>
    /// Fired when a direct child folder's shell icon or desktop.ini changes
    /// (e.g. via Explorer Properties or Folder Painter).
    /// The argument is the child folder path. Always raised on the UI thread.
    /// </summary>
    public event Action<string>? FolderIconChanged;

    /// <summary>
    /// The folder path currently being watched.
    /// </summary>
    public string? WatchedPath { get; private set; }

    public bool IsWatching => _legacyWatcher is not null || _shellNotifySubscription is not null;

    public int Generation
    {
        get
        {
            lock (_lock)
            {
                return _watchGeneration;
            }
        }
    }

    public bool IsReconnectPending
    {
        get
        {
            lock (_lock)
            {
                return !string.IsNullOrWhiteSpace(_reconnectPath);
            }
        }
    }

    public int ReconnectCount => Volatile.Read(ref _reconnectCount);
    public DateTimeOffset? LastEventAt => _lastEventAt;
    public FolderWatcherHealthSnapshot Health => new(
        WatchedPath,
        _health,
        _legacyWatcher is not null,
        _shellNotifySubscription is not null,
        IsReconnectPending,
        ReconnectCount,
        LastEventAt,
        _lastError);

    public FolderWatcherService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _debounceTimer = dispatcherQueue.CreateTimer();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(DebounceDelayMs);
        _debounceTimer.IsRepeating = false;
        _debounceTimer.Tick += DebounceTimer_Tick;

        _iconDebounceTimer = dispatcherQueue.CreateTimer();
        _iconDebounceTimer.Interval = TimeSpan.FromMilliseconds(DebounceDelayMs);
        _iconDebounceTimer.IsRepeating = false;
        _iconDebounceTimer.Tick += IconDebounceTimer_Tick;

        _reconnectTimer = dispatcherQueue.CreateTimer();
        _reconnectTimer.IsRepeating = false;
        _reconnectTimer.Tick += ReconnectTimer_Tick;
    }

    /// <summary>
    /// Start watching a folder for changes.
    /// </summary>
    public async Task StartAsync(string folderPath)
    {
        if (_isDisposed)
        {
            return;
        }

        string requestedPath = folderPath;
        // Watch the physical target rather than asking FileSystemWatcher to
        // traverse a user-created mount point. The logical junction path
        // remains in widget configuration; only this runtime watcher path is resolved.
        if (FileService.TryResolveExistingPathForTraversal(
                folderPath,
                out string traversalPath))
        {
            folderPath = traversalPath;
        }

        Stop();
        lock (_lock)
        {
            // Keep the logical path for reconnects. A version-manager junction
            // (for example Scoop's "current") can retarget while the app is
            // running; retrying the old physical target would never recover.
            _requestedPath = requestedPath;
        }
        _lastError = null;
        int startGeneration;
        lock (_lock)
        {
            startGeneration = _watchGeneration;
        }

        (FolderWatcherHealth availability, ShellChangeNotificationService.PreparedTarget? rawPreparedTarget) =
            await ProbeFolderAndPrepareShellTargetAsync(folderPath);
        using ShellChangeNotificationService.PreparedTarget? preparedShellTarget = rawPreparedTarget;
        lock (_lock)
        {
            if (_isDisposed || startGeneration != _watchGeneration)
            {
                return;
            }
        }

        if (availability != FolderWatcherHealth.Watching)
        {
            _health = availability;
            BeginReconnect(folderPath, resetAttempt: true);
            App.Log($"[FolderWatcher] Folder unavailable; reconnect scheduled for '{folderPath}'");
            return;
        }

        int generation;
        lock (_lock)
        {
            if (startGeneration != _watchGeneration)
            {
                return;
            }

            WatchedPath = folderPath;
            generation = _watchGeneration;
        }

        bool nativeStarted = TryStartLegacyWatcher(folderPath);
        bool shellStarted = TryStartShellNotificationSubscription(folderPath, generation, preparedShellTarget);
        if (!nativeStarted && !shellStarted)
        {
            _health = ProbeFolderAccess(folderPath) == FolderWatcherHealth.AccessDenied
                ? FolderWatcherHealth.AccessDenied
                : FolderWatcherHealth.Unavailable;
            BeginReconnect(folderPath);
        }
        else
        {
            _health = nativeStarted
                ? FolderWatcherHealth.Watching
                : FolderWatcherHealth.Degraded;
            lock (_lock)
            {
                _reconnectPath = null;
                _reconnectAttempt = 0;
            }
        }

        App.LogVerbose(
            $"[FolderWatcher] Started watcher for '{folderPath}' " +
            $"native={nativeStarted} shellNotify={shellStarted}");
    }

    private static FolderWatcherHealth ProbeFolderAccess(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
            {
                // Directory.Exists masks UnauthorizedAccessException, so force
                // an access check before classifying an unavailable path.
                _ = Directory.EnumerateFileSystemEntries(folderPath).Take(1).ToList();
                return FolderWatcherHealth.Unavailable;
            }

            return FolderWatcherHealth.Watching;
        }
        catch (UnauthorizedAccessException)
        {
            return FolderWatcherHealth.AccessDenied;
        }
        catch
        {
            return FolderWatcherHealth.Unavailable;
        }
    }

    private static Task<FolderWatcherHealth> ProbeFolderAccessAsync(string folderPath)
    {
        return Task.Run(() => ProbeFolderAccess(folderPath));
    }

    private static Task<(FolderWatcherHealth Availability, ShellChangeNotificationService.PreparedTarget? ShellTarget)>
        ProbeFolderAndPrepareShellTargetAsync(string folderPath)
    {
        return Task.Run(() =>
        {
            FolderWatcherHealth availability = ProbeFolderAccess(folderPath);
            if (availability != FolderWatcherHealth.Watching)
            {
                return (availability, (ShellChangeNotificationService.PreparedTarget?)null);
            }

            return (availability, ShellChangeNotificationService.Shared.PrepareTarget(folderPath));
        });
    }

    private bool TryStartShellNotificationSubscription(
        string folderPath,
        int generation,
        ShellChangeNotificationService.PreparedTarget? preparedShellTarget)
    {
        try
        {
            IDisposable? subscription = ShellChangeNotificationService.Shared.Subscribe(
                folderPath,
                _dispatcherQueue,
                notification => OnShellFolderNotification(notification, generation),
                preparedShellTarget);
            if (subscription is null)
            {
                return false;
            }

            lock (_lock)
            {
                if (_isDisposed ||
                    generation != _watchGeneration ||
                    !string.Equals(WatchedPath, folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    subscription.Dispose();
                    return false;
                }

                _shellNotifySubscription = subscription;
            }

            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            App.LogVerbose($"[FolderWatcher] ShellChangeNotify subscription failed: {ex.Message}");
            return false;
        }
    }

    private void OnShellFolderNotification(ShellFolderNotification notification, int generation)
    {
        lock (_lock)
        {
            if (_isDisposed ||
                string.IsNullOrWhiteSpace(WatchedPath) ||
                generation != _watchGeneration)
            {
                return;
            }
        }

        _lastEventAt = DateTimeOffset.Now;
        switch (notification.Kind)
        {
            case ShellFolderNotificationKind.Created when !string.IsNullOrWhiteSpace(notification.Path):
                QueueChange(new FolderChange(notification.Path, WatcherChangeTypes.Created), generation);
                break;

            case ShellFolderNotificationKind.Deleted when !string.IsNullOrWhiteSpace(notification.Path):
                QueueChange(new FolderChange(notification.Path, WatcherChangeTypes.Deleted), generation);
                break;

            case ShellFolderNotificationKind.Renamed when !string.IsNullOrWhiteSpace(notification.Path):
                QueueChange(
                    new FolderChange(notification.Path, WatcherChangeTypes.Renamed, notification.OldPath),
                    generation);
                break;

            case ShellFolderNotificationKind.Updated when !string.IsNullOrWhiteSpace(notification.Path):
                QueueChange(new FolderChange(notification.Path, WatcherChangeTypes.Changed), generation);
                if (Directory.Exists(notification.Path))
                {
                    QueueIconChange(notification.Path, generation);
                }
                break;

            case ShellFolderNotificationKind.DirectoryReload:
                if (!HandleUnavailableRootFromCallback(generation))
                {
                    QueueFullReload(generation);
                }
                break;

            case ShellFolderNotificationKind.FolderIconChanged when !string.IsNullOrWhiteSpace(notification.Path):
                QueueIconChange(notification.Path, generation);
                break;
        }
    }

    private bool TryGetActiveGeneration(
        object sender,
        object? activeWatcher,
        out int generation)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(sender, activeWatcher) ||
                string.IsNullOrWhiteSpace(WatchedPath))
            {
                generation = 0;
                return false;
            }

            generation = _watchGeneration;
            return true;
        }
    }

    private void QueueIconChange(string childFolderPath, int generation)
    {
        if (string.IsNullOrWhiteSpace(childFolderPath))
        {
            return;
        }

        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(WatchedPath) ||
                generation != _watchGeneration)
            {
                return;
            }

            _pendingIconPaths.Add(childFolderPath);
            _pendingGeneration = generation;
        }

        EnqueueIconDebounceTimerStart();
    }

    private bool TryStartLegacyWatcher(string folderPath)
    {
        try
        {
            _legacyWatcher = new FileSystemWatcher
            {
                Path = folderPath,
                NotifyFilter = NativeNotifyFilter,
                IncludeSubdirectories = false,
                InternalBufferSize = NativeBufferSizeBytes,
                EnableRaisingEvents = false
            };

            _legacyWatcher.Created += OnChanged;
            _legacyWatcher.Deleted += OnChanged;
            _legacyWatcher.Renamed += OnRenamed;
            _legacyWatcher.Changed += OnChanged;
            _legacyWatcher.Error += OnWatcherError;
            _legacyWatcher.EnableRaisingEvents = true;
            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            App.Log($"[FolderWatcher] Native watcher failed for '{folderPath}': {ex}");
            StopLegacyWatcher();
            return false;
        }
    }

    /// <summary>
    /// Stop watching the current folder.
    /// </summary>
    public void Stop()
    {
        _debounceTimer.Stop();
        _iconDebounceTimer.Stop();
        _reconnectTimer.Stop();

        IDisposable? shellSubscription;
        lock (_lock)
        {
            _watchGeneration++;
            _pendingChanges.Clear();
            _pendingIconPaths.Clear();
            _pendingGeneration = 0;
            _requiresFullReload = false;
            _firstPendingChangeTick = 0;
            _legacyRestartQueued = false;
            _reconnectPath = null;
            _requestedPath = null;
            _reconnectAttempt = 0;
            shellSubscription = _shellNotifySubscription;
            _shellNotifySubscription = null;
        }

        shellSubscription?.Dispose();
        StopLegacyWatcher();
        WatchedPath = null;
        _health = FolderWatcherHealth.Stopped;
    }

    private void StopLegacyWatcher()
    {
        if (_legacyWatcher is null)
        {
            return;
        }

        _legacyWatcher.EnableRaisingEvents = false;
        _legacyWatcher.Created -= OnChanged;
        _legacyWatcher.Deleted -= OnChanged;
        _legacyWatcher.Renamed -= OnRenamed;
        _legacyWatcher.Changed -= OnChanged;
        _legacyWatcher.Error -= OnWatcherError;
        _legacyWatcher.Dispose();
        _legacyWatcher = null;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!TryGetActiveGeneration(sender, _legacyWatcher, out int generation))
        {
            return;
        }

        _lastEventAt = DateTimeOffset.Now;
        if (HandleUnavailableRootFromCallback(generation))
        {
            return;
        }

        QueueChange(new FolderChange(e.FullPath, e.ChangeType), generation);
        if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath))
        {
            QueueIconChange(e.FullPath, generation);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!TryGetActiveGeneration(sender, _legacyWatcher, out int generation))
        {
            return;
        }

        _lastEventAt = DateTimeOffset.Now;
        if (HandleUnavailableRootFromCallback(generation))
        {
            return;
        }

        QueueChange(
            new FolderChange(e.FullPath, WatcherChangeTypes.Renamed, e.OldFullPath),
            generation);
    }

    private bool HandleUnavailableRootFromCallback(int generation)
    {
        string? path;
        lock (_lock)
        {
            path = WatchedPath;
        }

        if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
        {
            return false;
        }

        lock (_lock)
        {
            _health = FolderWatcherHealth.Unavailable;
            _lastError = "The watched folder is temporarily unavailable.";
        }

        // FileSystemWatcher callbacks run on a worker thread. Both reload and
        // reconnect paths marshal their DispatcherQueueTimer work explicitly.
        QueueFullReload(generation);
        BeginReconnect(path);
        return true;
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (!TryGetActiveGeneration(sender, _legacyWatcher, out int generation))
        {
            return;
        }

        _lastError = e.GetException()?.Message;
        _health = FolderWatcherHealth.Degraded;
        if (_legacyErrorAnnounced)
        {
            // A denied subtree keeps tripping the watcher every cycle; after
            // the first announcement only verbose logging is warranted.
            App.LogVerbose($"[FolderWatcher] Watcher error: {e.GetException()?.Message}");
        }
        else
        {
            _legacyErrorAnnounced = true;
            App.Log($"[FolderWatcher] Watcher error: {e.GetException()}");
        }

        QueueFullReload(generation);

        string? path;
        lock (_lock)
        {
            if (_legacyRestartQueued || string.IsNullOrWhiteSpace(WatchedPath))
            {
                return;
            }

            _legacyRestartQueued = true;
            path = WatchedPath;
        }

        _dispatcherQueue.TryEnqueue(async () =>
        {
            await RestartLegacyWatcherAsync(path, generation);
        });
    }

    private async Task RestartLegacyWatcherAsync(string path, int generation)
    {
        try
        {
            // A denied subdirectory makes the probe pass (the root lists fine)
            // while the watch loop keeps failing. Restarting at event speed
            // would spin forever; fall back to the backed-off reconnect loop,
            // which both spaces attempts out and preserves self-healing once
            // the ACL or drive returns to normal.
            TimeSpan sinceLastRestart = DateTimeOffset.UtcNow - _lastLegacyRestartAtUtc;
            if (sinceLastRestart < TimeSpan.FromSeconds(LegacyWatcherRestartMinIntervalSeconds))
            {
                lock (_lock)
                {
                    _legacyRestartQueued = false;
                }

                BeginReconnect(path);
                return;
            }

            _lastLegacyRestartAtUtc = DateTimeOffset.UtcNow;
            FolderWatcherHealth availability = await ProbeFolderAccessAsync(path);
            lock (_lock)
            {
                _legacyRestartQueued = false;
                if (_watchGeneration != generation ||
                    !string.Equals(WatchedPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            StopLegacyWatcher();
            if (availability == FolderWatcherHealth.Watching &&
                TryStartLegacyWatcher(path))
            {
                _health = FolderWatcherHealth.Watching;
                App.Log($"[FolderWatcher] Native watcher restarted for '{path}'");
            }
            else
            {
                BeginReconnect(path);
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _legacyRestartQueued = false;
                _lastError = ex.Message;
            }

            App.Log($"[FolderWatcher] Native watcher restart failed for '{path}': {ex}");
            BeginReconnect(path);
        }
    }

    private void BeginReconnect(string path, bool resetAttempt = false)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (_lock)
        {
            WatchedPath = path;
            _reconnectPath = path;
            if (resetAttempt)
            {
                _reconnectAttempt = 0;
            }
        }

        ScheduleReconnect();
    }

    private void ScheduleReconnect()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(ScheduleReconnect);
            return;
        }

        int attempt;
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(_reconnectPath))
            {
                return;
            }

            _reconnectAttempt = Math.Min(MaxReconnectAttempts, _reconnectAttempt + 1);
            attempt = _reconnectAttempt;
        }

        _reconnectTimer.Stop();
        _reconnectTimer.Interval = TimeSpan.FromSeconds(
            Math.Min(
                ReconnectPlateauSeconds,
                ReconnectBaseDelaySeconds * Math.Pow(2, attempt - 1)));
        _reconnectTimer.Start();
    }

    private async void ReconnectTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            _reconnectTimer.Stop();
            string? path;
            string? requestedPath;
            lock (_lock)
            {
                path = _reconnectPath;
                requestedPath = _requestedPath;
            }

            if (_isDisposed || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string probePath = path;
            if (!string.IsNullOrWhiteSpace(requestedPath) &&
                FileService.TryResolveExistingPathForTraversal(
                    requestedPath,
                    out string refreshedPath))
            {
                probePath = refreshedPath;
            }

            FolderWatcherHealth availability = await ProbeFolderAccessAsync(probePath);
            if (availability != FolderWatcherHealth.Watching)
            {
                _health = availability;
                ScheduleReconnect();
                return;
            }

            Interlocked.Increment(ref _reconnectCount);
            await StartAsync(requestedPath ?? path);
            if (!_isDisposed && IsWatching)
            {
                QueueFullReload();
                App.Log($"[FolderWatcher] Reconnected to '{WatchedPath ?? probePath}'");
            }
        }
        catch (Exception ex)
        {
            string? path;
            lock (_lock)
            {
                path = _reconnectPath;
                _health = FolderWatcherHealth.Unavailable;
                _lastError = ex.Message;
            }

            App.Log($"[FolderWatcher] Reconnect failed for '{path}': {ex}");
            if (!string.IsNullOrWhiteSpace(path))
            {
                BeginReconnect(path);
            }
        }
    }

    private void QueueChange(FolderChange change, int generation)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(WatchedPath) ||
                generation != _watchGeneration)
            {
                return;
            }

            if (_pendingChanges.Count == 0 && !_requiresFullReload)
            {
                _pendingGeneration = generation;
            }

            if (_firstPendingChangeTick == 0)
            {
                _firstPendingChangeTick = Environment.TickCount64;
            }

            if (!_requiresFullReload)
            {
                bool coalescedDuplicateChanged =
                    change.ChangeType == WatcherChangeTypes.Changed &&
                    HasPendingChangeForPath(change.FullPath);
                if (!coalescedDuplicateChanged)
                {
                    _pendingChanges.Add(change);
                    if (_pendingChanges.Count > MaxBufferedChangesBeforeReload)
                    {
                        _requiresFullReload = true;
                    }
                }
            }
        }

        // Restart the debounce timer — each new change resets the wait period
        // up to MaxDebounceWaitMs so sustained write storms still flush periodically.
        EnqueueDebounceTimerRestart();
    }

    private bool HasPendingChangeForPath(string fullPath)
    {
        for (int index = 0; index < _pendingChanges.Count; index++)
        {
            FolderChange existing = _pendingChanges[index];
            if ((existing.ChangeType is WatcherChangeTypes.Changed
                    or WatcherChangeTypes.Created
                    or WatcherChangeTypes.Renamed) &&
                string.Equals(existing.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void QueueFullReload(int? generation = null)
    {
        lock (_lock)
        {
            int effectiveGeneration = generation ?? _watchGeneration;
            if (string.IsNullOrWhiteSpace(WatchedPath) ||
                effectiveGeneration != _watchGeneration)
            {
                return;
            }

            _pendingGeneration = effectiveGeneration;
            _requiresFullReload = true;
            if (_firstPendingChangeTick == 0)
            {
                _firstPendingChangeTick = Environment.TickCount64;
            }
        }

        EnqueueDebounceTimerRestart();
    }

    private void EnqueueDebounceTimerRestart()
    {
        if (_isDisposed || Interlocked.Exchange(ref _debounceRestartQueued, 1) != 0)
        {
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(RestartDebounceTimer))
        {
            Interlocked.Exchange(ref _debounceRestartQueued, 0);
        }
    }

    private void EnqueueIconDebounceTimerStart()
    {
        if (_isDisposed || Interlocked.Exchange(ref _iconDebounceStartQueued, 1) != 0)
        {
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(StartIconDebounceTimer))
        {
            Interlocked.Exchange(ref _iconDebounceStartQueued, 0);
        }
    }

    private void RestartDebounceTimer()
    {
        Interlocked.Exchange(ref _debounceRestartQueued, 0);
        if (_isDisposed || string.IsNullOrWhiteSpace(WatchedPath))
        {
            return;
        }

        long firstTick;
        lock (_lock)
        {
            firstTick = _firstPendingChangeTick;
        }

        if (_debounceTimer.IsRunning &&
            firstTick != 0 &&
            Environment.TickCount64 - firstTick >= MaxDebounceWaitMs)
        {
            return;
        }

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void StartIconDebounceTimer()
    {
        Interlocked.Exchange(ref _iconDebounceStartQueued, 0);
        if (_isDisposed || string.IsNullOrWhiteSpace(WatchedPath))
        {
            return;
        }

        _iconDebounceTimer.Start();
    }

    private void DebounceTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        FolderChangeBatch batch;
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(WatchedPath))
            {
                return;
            }

            batch = new FolderChangeBatch(
                WatchedPath,
                _pendingChanges.ToList(),
                _requiresFullReload,
                _pendingGeneration == 0 ? _watchGeneration : _pendingGeneration);
            _pendingChanges.Clear();
            _pendingGeneration = 0;
            _requiresFullReload = false;
            _firstPendingChangeTick = 0;
        }

        if (batch.Changes.Count == 0 && !batch.RequiresFullReload)
        {
            return;
        }

        FolderChanged?.Invoke(batch);
    }

    private void IconDebounceTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        List<string> iconPaths;
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(WatchedPath))
            {
                return;
            }

            iconPaths = _pendingIconPaths.ToList();
            _pendingIconPaths.Clear();
        }

        foreach (var path in iconPaths)
        {
            FolderIconChanged?.Invoke(path);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Stop();
        _debounceTimer.Stop();
        _debounceTimer.Tick -= DebounceTimer_Tick;
        _iconDebounceTimer.Stop();
        _iconDebounceTimer.Tick -= IconDebounceTimer_Tick;
        _reconnectTimer.Stop();
        _reconnectTimer.Tick -= ReconnectTimer_Tick;
    }
}
