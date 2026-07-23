using Microsoft.UI.Dispatching;
using Windows.Storage;
using Windows.Storage.Search;

namespace DeskBox.Services;

public sealed record FolderChange(string FullPath, WatcherChangeTypes ChangeType, string? OldFullPath = null);

public sealed record FolderChangeBatch(string WatchedPath, IReadOnlyList<FolderChange> Changes, bool RequiresFullReload);

/// <summary>
/// Watches a folder for file system changes and notifies via events.
/// Uses <see cref="StorageFileQueryResult"/> as the primary watcher for
/// better performance on large/indexed directories, falling back to
/// <see cref="FileSystemWatcher"/> for unsupported paths (network, etc.).
/// Implements debouncing using a DispatcherQueueTimer to avoid creating
/// short-lived thread-pool tasks on every file-system event.
/// </summary>
public sealed class FolderWatcherService : IDisposable
{
    private const int DebounceDelayMs = 250;
    private const int MaxBufferedChangesBeforeReload = 64;

    private FileSystemWatcher? _legacyWatcher;
    private FileSystemWatcher? _desktopIniWatcher;
    private StorageFileQueryResult? _queryWatcher;
    private readonly DispatcherQueueTimer _debounceTimer;
    private readonly DispatcherQueueTimer _iconDebounceTimer;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly object _lock = new();
    private readonly List<FolderChange> _pendingChanges = [];
    private readonly HashSet<string> _pendingIconPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _requiresFullReload;

    /// <summary>
    /// Fired when the watched folder's contents change (debounced).
    /// Always raised on the UI thread.
    /// </summary>
    public event Action<FolderChangeBatch>? FolderChanged;

    /// <summary>
    /// Fired when a direct child folder's desktop.ini changes, which means
    /// its shell icon may have been customized (e.g. via Folder Painter).
    /// The argument is the child folder path.  Always raised on the UI thread.
    /// </summary>
    public event Action<string>? FolderIconChanged;

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

        _iconDebounceTimer = dispatcherQueue.CreateTimer();
        _iconDebounceTimer.Interval = TimeSpan.FromMilliseconds(DebounceDelayMs);
        _iconDebounceTimer.IsRepeating = false;
        _iconDebounceTimer.Tick += IconDebounceTimer_Tick;
    }

    /// <summary>
    /// Start watching a folder for changes.
    /// </summary>
    public async Task StartAsync(string folderPath)
    {
        Stop();

        if (!Directory.Exists(folderPath)) return;

        WatchedPath = folderPath;

        StartDesktopIniWatcher(folderPath);

        if (await TryStartQueryWatcherAsync(folderPath))
        {
            App.LogVerbose($"[FolderWatcher] Using StorageFileQueryResult for '{folderPath}'");
            return;
        }

        App.LogVerbose($"[FolderWatcher] Falling back to FileSystemWatcher for '{folderPath}'");
        StartLegacyWatcher(folderPath);
    }

    /// <summary>
    /// Attempt to create a StorageFileQueryResult for the folder.
    /// This leverages the Windows search index for better performance.
    /// </summary>
    private async Task<bool> TryStartQueryWatcherAsync(string folderPath)
    {
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            if (folder is null)
            {
                return false;
            }

            var options = new QueryOptions
            {
                FolderDepth = FolderDepth.Shallow,
                IndexerOption = IndexerOption.UseIndexerWhenAvailable,
            };

            _queryWatcher = folder.CreateFileQueryWithOptions(options);
            _queryWatcher.ContentsChanged += OnQueryContentsChanged;
            return true;
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[FolderWatcher] StorageFileQueryResult creation failed: {ex.Message}");
            _queryWatcher = null;
            return false;
        }
    }

    private void OnQueryContentsChanged(IStorageQueryResultBase sender, object args)
    {
        // StorageFileQueryResult.ContentsChanged does not provide details
        // about what changed — it only signals that something in the folder
        // changed.  We treat this as a full-reload signal.
        QueueFullReload();
    }

    /// <summary>
    /// Starts a recursive watcher that only reports desktop.ini changes.
    /// A child folder's shell icon is driven by its own desktop.ini, so this
    /// detects icon customization tools (e.g. Folder Painter) which the
    /// shallow content watcher above cannot see.
    /// </summary>
    private void StartDesktopIniWatcher(string folderPath)
    {
        try
        {
            _desktopIniWatcher = new FileSystemWatcher
            {
                Path = folderPath,
                Filter = "desktop.ini",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName |
                               NotifyFilters.CreationTime,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };
            _desktopIniWatcher.Created += OnDesktopIniChanged;
            _desktopIniWatcher.Changed += OnDesktopIniChanged;
            _desktopIniWatcher.Renamed += OnDesktopIniChanged;
            _desktopIniWatcher.Error += OnDesktopIniWatcherError;
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[FolderWatcher] desktop.ini watcher failed for '{folderPath}': {ex.Message}");
            _desktopIniWatcher = null;
        }
    }

    private void OnDesktopIniChanged(object sender, FileSystemEventArgs e)
    {
        // Only direct child folders' icons are displayed — ignore deeper
        // nesting and a desktop.ini sitting at the watched root itself.
        string? childDir = Path.GetDirectoryName(e.FullPath);
        if (string.IsNullOrEmpty(childDir))
        {
            return;
        }

        string? parent = Path.GetDirectoryName(childDir);
        if (!string.Equals(parent, WatchedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(WatchedPath))
            {
                return;
            }

            _pendingIconPaths.Add(childDir);
        }

        _dispatcherQueue.TryEnqueue(() => _iconDebounceTimer.Start());
    }

    private void OnDesktopIniWatcherError(object sender, ErrorEventArgs e)
    {
        App.Log($"[FolderWatcher] desktop.ini watcher error: {e.GetException()}");
    }

    private void StartLegacyWatcher(string folderPath)
    {
        _legacyWatcher = new FileSystemWatcher
        {
            Path = folderPath,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.CreationTime |
                           NotifyFilters.Attributes,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _legacyWatcher.Created += OnChanged;
        _legacyWatcher.Deleted += OnChanged;
        _legacyWatcher.Renamed += OnRenamed;
        _legacyWatcher.Changed += OnChanged;
        _legacyWatcher.Error += OnWatcherError;
    }

    /// <summary>
    /// Stop watching the current folder.
    /// </summary>
    public void Stop()
    {
        _debounceTimer.Stop();
        _iconDebounceTimer.Stop();

        lock (_lock)
        {
            _pendingChanges.Clear();
            _pendingIconPaths.Clear();
            _requiresFullReload = false;
        }

        if (_queryWatcher is not null)
        {
            _queryWatcher.ContentsChanged -= OnQueryContentsChanged;
            // StorageFileQueryResult is a WinRT COM object (not IDisposable).
            // Explicitly release the RCW to avoid leaking the native query handle
            // until the next GC. This is especially important when switching
            // mapped folder paths, which calls Stop()+StartAsync() repeatedly.
            try { System.Runtime.InteropServices.Marshal.ReleaseComObject(_queryWatcher); }
            catch { }
            _queryWatcher = null;
        }

        if (_desktopIniWatcher is not null)
        {
            _desktopIniWatcher.EnableRaisingEvents = false;
            _desktopIniWatcher.Created -= OnDesktopIniChanged;
            _desktopIniWatcher.Changed -= OnDesktopIniChanged;
            _desktopIniWatcher.Renamed -= OnDesktopIniChanged;
            _desktopIniWatcher.Error -= OnDesktopIniWatcherError;
            _desktopIniWatcher.Dispose();
            _desktopIniWatcher = null;
        }

        if (_legacyWatcher is not null)
        {
            _legacyWatcher.EnableRaisingEvents = false;
            _legacyWatcher.Created -= OnChanged;
            _legacyWatcher.Deleted -= OnChanged;
            _legacyWatcher.Renamed -= OnRenamed;
            _legacyWatcher.Changed -= OnChanged;
            _legacyWatcher.Error -= OnWatcherError;
            _legacyWatcher.Dispose();
            _legacyWatcher = null;
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
        Stop();
        _debounceTimer.Stop();
        _debounceTimer.Tick -= DebounceTimer_Tick;
        _iconDebounceTimer.Stop();
        _iconDebounceTimer.Tick -= IconDebounceTimer_Tick;
    }
}
