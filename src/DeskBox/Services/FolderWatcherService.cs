namespace DeskBox.Services;

public sealed record FolderChange(string FullPath, WatcherChangeTypes ChangeType, string? OldFullPath = null);

public sealed record FolderChangeBatch(string WatchedPath, IReadOnlyList<FolderChange> Changes, bool RequiresFullReload);

/// <summary>
/// Watches a folder for file system changes and notifies via events.
/// Implements debouncing to avoid duplicate event handling.
/// </summary>
public sealed class FolderWatcherService : IDisposable
{
    private const int DebounceDelayMs = 250;
    private const int MaxBufferedChangesBeforeReload = 64;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
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

    public FolderWatcherService(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
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
        lock (_lock)
        {
            _debounceCts?.Cancel();
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
        CancellationToken token;
        string? watchedPath;
        lock (_lock)
        {
            watchedPath = WatchedPath;
            if (string.IsNullOrWhiteSpace(watchedPath))
            {
                return;
            }

            _pendingChanges.Add(change);
            if (_pendingChanges.Count > MaxBufferedChangesBeforeReload)
            {
                _requiresFullReload = true;
            }

            token = ResetDebounceTokenUnsafe();
        }

        ScheduleDispatch(watchedPath, token);
    }

    private void QueueFullReload()
    {
        CancellationToken token;
        string? watchedPath;
        lock (_lock)
        {
            watchedPath = WatchedPath;
            if (string.IsNullOrWhiteSpace(watchedPath))
            {
                return;
            }

            _requiresFullReload = true;
            token = ResetDebounceTokenUnsafe();
        }

        ScheduleDispatch(watchedPath, token);
    }

    private CancellationToken ResetDebounceTokenUnsafe()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        return _debounceCts.Token;
    }

    private void ScheduleDispatch(string watchedPath, CancellationToken token)
    {
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelayMs, token);
                if (!token.IsCancellationRequested)
                {
                    FolderChangeBatch batch;
                    lock (_lock)
                    {
                        batch = new FolderChangeBatch(
                            watchedPath,
                            _pendingChanges.ToList(),
                            _requiresFullReload);
                        _pendingChanges.Clear();
                        _requiresFullReload = false;
                    }

                    _dispatcherQueue.TryEnqueue(() => FolderChanged?.Invoke(batch));
                }
            }
            catch (TaskCanceledException) { }
        });
    }

    public void Dispose()
    {
        Stop();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
    }
}
