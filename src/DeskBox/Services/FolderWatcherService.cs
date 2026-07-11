using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

public sealed record FolderChange(string FullPath, WatcherChangeTypes ChangeType, string? OldFullPath = null);

public sealed record FolderChangeBatch(string WatchedPath, IReadOnlyList<FolderChange> Changes, bool RequiresFullReload);

/// <summary>
/// Watches a folder for file system changes and notifies via events.
/// Implements debouncing using a DispatcherQueueTimer to avoid creating
/// short-lived thread-pool tasks on every file-system event.
/// </summary>
public sealed class FolderWatcherService : IDisposable
{
    private const int DebounceDelayMs = 250;
    private const int MaxBufferedChangesBeforeReload = 64;

    private FileSystemWatcher? _watcher;
    private readonly DispatcherQueueTimer _debounceTimer;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly object _lock = new();
    private readonly List<FolderChange> _pendingChanges = [];
    private bool _requiresFullReload;

    /// <summary>
    /// Fired when the watched folder's contents change (debounced).
    /// Always raised on the UI thread.
    /// </summary>
    public event Action<FolderChangeBatch>? FolderChanged;

    /// <summary>
    /// The folder path currently being watched.
    /// </summary>
    public string? WatchedPath { get; private set; }

    public FolderWatcherService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _debounceTimer = dispatcherQueue.CreateTimer();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(DebounceDelayMs);
        _debounceTimer.IsRepeating = false;
        _debounceTimer.Tick += DebounceTimer_Tick;
    }

    /// <summary>
    /// Start watching a folder for changes.
    /// </summary>
    public void Start(string folderPath)
    {
        Stop();

        if (!Directory.Exists(folderPath)) return;

        WatchedPath = folderPath;
        _watcher = new FileSystemWatcher
        {
            Path = folderPath,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.CreationTime |
                           NotifyFilters.Attributes,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Changed += OnChanged;
        _watcher.Error += OnWatcherError;
    }

    /// <summary>
    /// Stop watching the current folder.
    /// </summary>
    public void Stop()
    {
        _debounceTimer.Stop();

        lock (_lock)
        {
            _pendingChanges.Clear();
            _requiresFullReload = false;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Changed -= OnChanged;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }
        WatchedPath = null;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        QueueChange(new FolderChange(e.FullPath, e.ChangeType));
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        QueueChange(new FolderChange(e.FullPath, WatcherChangeTypes.Renamed, e.OldFullPath));
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        App.Log($"[FolderWatcher] Watcher error: {e.GetException()}");
        QueueFullReload();
    }

    private void QueueChange(FolderChange change)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(WatchedPath))
            {
                return;
            }

            _pendingChanges.Add(change);
            if (_pendingChanges.Count > MaxBufferedChangesBeforeReload)
            {
                _requiresFullReload = true;
            }
        }

        // Restart the debounce timer — each new change resets the wait period.
        _dispatcherQueue.TryEnqueue(() => _debounceTimer.Start());
    }

    private void QueueFullReload()
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(WatchedPath))
            {
                return;
            }

            _requiresFullReload = true;
        }

        _dispatcherQueue.TryEnqueue(() => _debounceTimer.Start());
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
                _requiresFullReload);
            _pendingChanges.Clear();
            _requiresFullReload = false;
        }

        FolderChanged?.Invoke(batch);
    }

    public void Dispose()
    {
        Stop();
        _debounceTimer.Stop();
        _debounceTimer.Tick -= DebounceTimer_Tick;
    }
}
