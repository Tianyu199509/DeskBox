using System.Collections.Concurrent;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Background file indexer that maintains an in-memory filename index
/// for fast search across user directories. The index is persisted to disk so
/// results are available immediately on launch, and subsequent scans reconcile
/// against the existing index (incremental update) instead of rebuilding from scratch.
/// </summary>
public sealed class SearchIndexService : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Hard cap on in-memory entries to prevent unbounded memory growth.</summary>
    private const int MaxIndexEntries = 300_000;

    /// <summary>Fallback depth for fixed-drive scans when the USN journal is unavailable.</summary>
    private const int DriveRootMaxDepth = 6;

    /// <summary>Max entries to persist to disk (avoid multi-hundred-MB JSON).</summary>
    private const int MaxPersistedEntries = 100_000;

    private readonly SettingsService _settingsService;
    private readonly ConcurrentDictionary<string, IndexedFileEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly string _storePath;
    private readonly ManualResetEventSlim _pauseGate = new(true);
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _saveCts;
    private Task? _scanTask;
    private bool _isDisposed;
    private int _isScanning;
    private int _isPaused;
    private bool _hasLoadedPersistedIndex;
    private int _scannedCount;
    private DateTime? _lastScanTime;

    public SearchIndexService(SettingsService settingsService)
        : this(
            settingsService,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskBox",
                "cache",
                "search-index.json"))
    {
    }

    internal SearchIndexService(SettingsService settingsService, string storePath)
    {
        _settingsService = settingsService;
        _storePath = storePath;
    }

    public bool IsScanning => Volatile.Read(ref _isScanning) == 1;

    public bool IsPaused => Volatile.Read(ref _isPaused) == 1;

    public int EntryCount => _index.Count;
    public int IndexedCount => _index.Count;

    /// <summary>Number of items scanned during the current/last scan pass.</summary>
    public int ScannedCount => Volatile.Read(ref _scannedCount);

    /// <summary>When the last full scan completed.</summary>
    public DateTime? LastScanTime => _lastScanTime;

    public event Action? IndexUpdated;

    /// <summary>Raised periodically during scanning with the current scanned count.</summary>
    public event Action<int>? ProgressChanged;

    /// <summary>
    /// Pauses an in-progress scan. The scan thread blocks until <see cref="ResumeIndexing"/> is called.
    /// </summary>
    public void PauseIndexing()
    {
        if (IsScanning && !IsPaused)
        {
            Volatile.Write(ref _isPaused, 1);
            _pauseGate.Reset();
        }
    }

    /// <summary>Resumes a paused scan.</summary>
    public void ResumeIndexing()
    {
        if (IsPaused)
        {
            Volatile.Write(ref _isPaused, 0);
            _pauseGate.Set();
        }
    }

    /// <summary>Clears the index and starts a fresh full scan.</summary>
    public void RebuildIndex()
    {
        StopIndexing();
        _index.Clear();
        Volatile.Write(ref _scannedCount, 0);
        _lastScanTime = null;
        StartIndexing();
    }

    /// <summary>Returns the on-disk size (bytes) of the persisted index file, or 0 if absent.</summary>
    public long GetIndexStorageBytes()
    {
        try
        {
            return File.Exists(_storePath) ? new FileInfo(_storePath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Loads the persisted index from disk (if present) so search returns results
    /// immediately, before the first background scan completes. Safe to call once.
    /// </summary>
    public void TryLoadPersistedIndex()
    {
        if (_hasLoadedPersistedIndex)
        {
            return;
        }

        _hasLoadedPersistedIndex = true;

        try
        {
            if (!File.Exists(_storePath))
            {
                return;
            }

            // Skip files that are too large (> 50 MB) to avoid loading hundreds of MB into memory.
            var fileInfo = new FileInfo(_storePath);
            if (fileInfo.Length > 50 * 1024 * 1024)
            {
                App.Log($"[SearchIndex] Persisted index too large ({fileInfo.Length / 1024 / 1024} MB). Skipping load; will rebuild.");
                return;
            }

            string json = File.ReadAllText(_storePath);
            var persisted = JsonSerializer.Deserialize<PersistedIndex>(json, s_jsonOptions);
            if (persisted?.Entries is not { Count: > 0 })
            {
                return;
            }

            foreach (var entry in persisted.Entries)
            {
                if (_index.Count >= MaxIndexEntries)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(entry.FullPath))
                {
                    continue;
                }

                _index[entry.FullPath] = new IndexedFileEntry(
                    entry.FileName,
                    entry.DirectoryPath,
                    entry.FullPath,
                    entry.IsDirectory,
                    entry.LastModified);
            }

            App.Log($"[SearchIndex] Loaded {_index.Count} persisted entries from disk.");
        }
        catch (Exception ex)
        {
            App.Log($"[SearchIndex] Failed to load persisted index: {ex.Message}");
        }
    }

    /// <summary>
    /// Persists the current in-memory index to disk.
    /// Skips persistence when the index is very large to avoid multi-hundred-MB JSON strings.
    /// </summary>
    public void SaveIndex()
    {
        try
        {
            if (_index.Count > MaxPersistedEntries)
            {
                App.Log($"[SearchIndex] Index too large to persist ({_index.Count} > {MaxPersistedEntries}). Skipping save.");
                return;
            }

            var persisted = new PersistedIndex
            {
                Entries = _index.Values
                    .Select(e => new PersistedIndex.Entry
                    {
                        FileName = e.FileName,
                        DirectoryPath = e.DirectoryPath,
                        FullPath = e.FullPath,
                        IsDirectory = e.IsDirectory,
                        LastModified = e.LastModified
                    })
                    .ToList()
            };

            string? directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(persisted, s_jsonOptions);
            File.WriteAllText(_storePath, json);
        }
        catch (Exception ex)
        {
            App.Log($"[SearchIndex] Failed to save index: {ex.Message}");
        }
    }

    /// <summary>
    /// Schedules a debounced save of the index (used after filesystem watcher changes).
    /// </summary>
    private void ScheduleSave()
    {
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000, token);
                if (!token.IsCancellationRequested)
                {
                    SaveIndex();
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer change.
            }
        }, token);
    }

    /// <summary>
    /// Starts the background indexing process.
    /// </summary>
    public void StartIndexing()
    {
        if (_isDisposed || IsScanning)
        {
            return;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        _scanTask = Task.Run(() => ScanDirectoriesAsync(token), token);
    }

    /// <summary>
    /// Stops indexing and clears the index.
    /// </summary>
    public void StopIndexing()
    {
        _scanCts?.Cancel();
        ClearWatchers();
    }

    /// <summary>
    /// Searches the index for files matching the query.
    /// </summary>
    public IReadOnlyList<SearchResultItem> Search(string query, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(query) || _index.IsEmpty)
        {
            return [];
        }

        string normalizedQuery = query.Trim();
        if (maxResults <= 0)
        {
            return [];
        }

        var topResults = new PriorityQueue<SearchResultItem, (double Score, long ModifiedTicks)>();

        foreach (var (_, entry) in _index)
        {
            double score = ComputeRelevance(entry.FileName, normalizedQuery);
            if (score <= 0)
            {
                continue;
            }

            var result = new SearchResultItem
            {
                Kind = entry.IsDirectory ? SearchResultKind.Folder : SearchResultKind.File,
                Title = entry.FileName,
                Subtitle = entry.DirectoryPath,
                DetailPath = entry.FullPath,
                ModifiedAt = entry.LastModified,
                RelevanceScore = score,
                Glyph = entry.IsDirectory ? "\uE8B7" : null
            };

            long modifiedTicks = entry.LastModified.ToUniversalTime().Ticks;
            topResults.Enqueue(result, (score, modifiedTicks));
            if (topResults.Count > maxResults)
            {
                topResults.Dequeue();
            }
        }

        return topResults.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(r => r.RelevanceScore)
            .ThenByDescending(r => r.ModifiedAt)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Gets recently modified files from the index.
    /// </summary>
    public IReadOnlyList<SearchResultItem> GetRecentFiles(int count)
    {
        return _index.Values
            .Where(e => !e.IsDirectory)
            .OrderByDescending(e => e.LastModified)
            .Take(count)
            .Select(e => new SearchResultItem
            {
                Kind = SearchResultKind.File,
                Title = e.FileName,
                Subtitle = e.DirectoryPath,
                DetailPath = e.FullPath,
                ModifiedAt = e.LastModified
            })
            .ToList();
    }

    /// <summary>
    /// Derives the most frequently occurring parent folders from the index.
    /// Folders that host more indexed files are treated as "frequently used" and
    /// surfaced as recommendations.
    /// </summary>
    public IReadOnlyList<SearchResultItem> GetFrequentFolders(int count)
    {
        if (_index.IsEmpty)
        {
            return [];
        }

        return _index.Values
            .Where(e => !e.IsDirectory && !string.IsNullOrWhiteSpace(e.DirectoryPath))
            .GroupBy(e => e.DirectoryPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Path = g.Key,
                FileCount = g.Count(),
                LastModified = g.Max(e => e.LastModified)
            })
            .OrderByDescending(f => f.FileCount)
            .ThenByDescending(f => f.LastModified)
            .Take(count)
            .Select(f => new SearchResultItem
            {
                Kind = SearchResultKind.Folder,
                Title = Path.GetFileName(f.Path),
                Subtitle = f.Path,
                DetailPath = f.Path,
                ModifiedAt = f.LastModified,
                Glyph = "\uE8B7"
            })
            .ToList();
    }

    private async Task ScanDirectoriesAsync(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _isScanning, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(ref _scannedCount, 0);

        try
        {
            var (userDirs, driveRoots) = GetScanDirectories();

            // Track every path observed during this scan so we can reconcile
            // (remove stale entries) instead of clearing and rebuilding the index.
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // User directories: full-depth scan (these are the primary search targets).
            foreach (string directory in userDirs)
            {
                if (token.IsCancellationRequested || _index.Count >= MaxIndexEntries)
                {
                    break;
                }

                if (!Directory.Exists(directory))
                {
                    continue;
                }

                await Task.Run(() => ScanDirectoryRecursive(directory, seenPaths, token, maxDepth: int.MaxValue), token);
            }

            // Drive roots: shallow scan (broad coverage without indexing millions of files).
            foreach (string drive in driveRoots)
            {
                if (token.IsCancellationRequested || _index.Count >= MaxIndexEntries)
                {
                    break;
                }

                if (!Directory.Exists(drive))
                {
                    continue;
                }

                await Task.Run(() => ScanDirectoryRecursive(drive, seenPaths, token, maxDepth: DriveRootMaxDepth), token);
            }

            if (!token.IsCancellationRequested)
            {
                var allRoots = new List<string>(userDirs);
                allRoots.AddRange(driveRoots);
                ReconcileIndex(allRoots, seenPaths);
                SetupWatchers(userDirs); // Only watch user directories (drive roots generate too many events)
                SaveIndex();
                _lastScanTime = DateTime.Now;
                IndexUpdated?.Invoke();
                App.Log($"[SearchIndex] Indexing complete. {_index.Count} entries.");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            App.Log($"[SearchIndex] Indexing error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isScanning, 0);
            Volatile.Write(ref _isPaused, 0);
            _pauseGate.Set();
        }
    }

    /// <summary>
    /// Removes indexed entries that live under a scanned root but were not observed
    /// in the latest scan (i.e., they were deleted or moved). Entries outside the
    /// current scan roots are left untouched.
    /// </summary>
    private void ReconcileIndex(List<string> scannedRoots, HashSet<string> seenPaths)
    {
        var staleKeys = new List<string>();

        foreach (var (path, _) in _index)
        {
            if (seenPaths.Contains(path))
            {
                continue;
            }

            bool underScannedRoot = scannedRoots.Any(root =>
                path.StartsWith(root, StringComparison.OrdinalIgnoreCase));

            if (underScannedRoot)
            {
                staleKeys.Add(path);
            }
        }

        foreach (string key in staleKeys)
        {
            _index.TryRemove(key, out _);
        }

        if (staleKeys.Count > 0)
        {
            App.Log($"[SearchIndex] Reconciled {staleKeys.Count} stale entries.");
        }
    }

    private void ScanDirectoryRecursive(string rootPath, HashSet<string> seenPaths, CancellationToken token, int maxDepth = int.MaxValue)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((rootPath, 0));
        int progressCounter = 0;

        var skipDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information", "node_modules",
            ".git", "obj", "bin", ".vs", ".artifacts",
            "Windows", "ProgramData", "Program Files", "Program Files (x86)",
            "Recovery", "PerfLogs", "Config.Msi", "MSOCache", "WinSxS",
            "servicing", "assembly", "Intel", "AMD"
        };

        while (queue.Count > 0)
        {
            if (token.IsCancellationRequested || _index.Count >= MaxIndexEntries)
            {
                return;
            }

            // Honor pause: block until resumed or cancelled.
            _pauseGate.Wait(token);

            var (current, depth) = queue.Dequeue();

            try
            {
                foreach (string file in Directory.EnumerateFiles(current))
                {
                    if (token.IsCancellationRequested || _index.Count >= MaxIndexEntries)
                    {
                        return;
                    }

                    TryAddEntry(file, isDirectory: false, seenPaths);

                    // Report progress every 200 files to avoid flooding the UI thread.
                    if (++progressCounter % 200 == 0)
                    {
                        Volatile.Write(ref _scannedCount, _index.Count);
                        ProgressChanged?.Invoke(_index.Count);
                    }
                }

                // Only recurse into subdirectories if we haven't hit the depth limit.
                if (depth < maxDepth)
                {
                    foreach (string dir in Directory.EnumerateDirectories(current))
                    {
                        string dirName = Path.GetFileName(dir);
                        if (skipDirectories.Contains(dirName) ||
                            (dirName.StartsWith('.') && dirName.Length > 1))
                        {
                            continue;
                        }

                        TryAddEntry(dir, isDirectory: true, seenPaths);
                        queue.Enqueue((dir, depth + 1));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we can't access
            }
            catch (IOException)
            {
                // Skip directories with I/O errors
            }
        }
    }

    private void TryAddEntry(string path, bool isDirectory, HashSet<string>? seenPaths = null)
    {
        try
        {
            var info = new FileInfo(path);
            string fileName = Path.GetFileName(path);
            string directoryPath = Path.GetDirectoryName(path) ?? string.Empty;

            var entry = new IndexedFileEntry(
                fileName,
                directoryPath,
                path,
                isDirectory,
                info.LastWriteTime);

            _index[path] = entry;
            seenPaths?.Add(path);
        }
        catch
        {
            // Skip entries we can't stat
        }
    }

    private (List<string> UserDirs, List<string> DriveRoots) GetScanDirectories()
    {
        var userDirs = new List<string>();
        var driveRoots = new List<string>();

        // Default: user profile directories (full-depth scan)
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            string[] defaultDirs =
            [
                Path.Combine(userProfile, "Desktop"),
                Path.Combine(userProfile, "Documents"),
                Path.Combine(userProfile, "Downloads"),
                Path.Combine(userProfile, "Pictures"),
                Path.Combine(userProfile, "Music"),
                Path.Combine(userProfile, "Videos"),
                Path.Combine(userProfile, "DeskBox")
            ];

            userDirs.AddRange(defaultDirs.Where(Directory.Exists));
        }

        // Applications and files explicitly surfaced by DeskBox should be searchable
        // even when they live outside the standard user libraries.
        foreach (var widget in _settingsService.Settings.Widgets
                     .Where(widget => widget.WidgetKind == WidgetKind.File && !widget.IsDisabled))
        {
            if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath) &&
                Directory.Exists(widget.MappedFolderPath))
            {
                userDirs.Add(widget.MappedFolderPath);
            }

            foreach (string parent in widget.Items
                         .Select(item => Path.GetDirectoryName(item.Path))
                         .OfType<string>()
                         .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
            {
                userDirs.Add(parent);
            }
        }

        string[] applicationRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        ];
        userDirs.AddRange(applicationRoots.Where(path =>
            !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)));

        // Custom paths from settings (full-depth scan)
        var customPaths = _settingsService.Settings.SearchCustomIndexPaths;
        if (customPaths is { Count: > 0 })
        {
            userDirs.AddRange(customPaths.Where(Directory.Exists));
        }

        // Broad fallback coverage: add every fixed drive root (shallow scan).
        // The USN journal index is preferred when available (it needs elevation);
        // this directory scan keeps near-full-disk coverage without admin.
        // System directories are excluded by name in ScanDirectoryRecursive.
        foreach (string drive in Directory.GetLogicalDrives())
        {
            try
            {
                var info = new DriveInfo(drive);
                if (info.IsReady && info.DriveType == DriveType.Fixed)
                {
                    driveRoots.Add(drive);
                }
            }
            catch
            {
                // Skip drives that cannot be queried.
            }
        }

        return (userDirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                driveRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private void SetupWatchers(List<string> directories)
    {
        ClearWatchers();

        foreach (string dir in directories)
        {
            try
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };

                watcher.Created += OnFileSystemChanged;
                watcher.Deleted += OnFileSystemChanged;
                watcher.Renamed += OnFileSystemRenamed;
                _watchers.Add(watcher);
            }
            catch
            {
                // Skip directories where watching fails
            }
        }
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType == WatcherChangeTypes.Deleted)
        {
            _index.TryRemove(e.FullPath, out _);
        }
        else
        {
            TryAddEntry(e.FullPath, Directory.Exists(e.FullPath));
        }

        ScheduleSave();
        IndexUpdated?.Invoke();
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        _index.TryRemove(e.OldFullPath, out _);
        TryAddEntry(e.FullPath, Directory.Exists(e.FullPath));
        ScheduleSave();
        IndexUpdated?.Invoke();
    }

    internal static double ComputeRelevance(string fileName, string query)
    {
        if (fileName.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 100.0;
        }

        if (fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 80.0;
        }

        string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        if (nameWithoutExt.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 90.0;
        }

        if (nameWithoutExt.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 70.0;
        }

        if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 50.0;
        }

        return 0;
    }

    private void ClearWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        StopIndexing();
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _scanCts?.Dispose();
        _pauseGate.Dispose();
        _index.Clear();
    }

    private sealed record IndexedFileEntry(
        string FileName,
        string DirectoryPath,
        string FullPath,
        bool IsDirectory,
        DateTime LastModified);

    /// <summary>
    /// On-disk representation of the filename index.
    /// </summary>
    private sealed class PersistedIndex
    {
        public List<Entry> Entries { get; set; } = [];

        public sealed class Entry
        {
            public string FileName { get; set; } = string.Empty;
            public string DirectoryPath { get; set; } = string.Empty;
            public string FullPath { get; set; } = string.Empty;
            public bool IsDirectory { get; set; }
            public DateTime LastModified { get; set; }
        }
    }
}
