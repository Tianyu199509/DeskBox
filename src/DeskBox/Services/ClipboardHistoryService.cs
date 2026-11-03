using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using ClipboardHistoryItem = DeskBox.Models.ClipboardHistoryItem;

namespace DeskBox.Services;

public sealed record ClipboardDoubleCopyEventArgs(
    ClipboardHistoryItem Item,
    string ActionMode);

/// <summary>
/// Singleton system clipboard history manager and persistence coordinator
/// backing <see cref="WidgetKind.Clipboard"/>.
/// </summary>
public sealed class ClipboardHistoryService : IDisposable
{
    private const uint ThumbnailMaxPixelSize = 320;
    private const int MaxObservedApps = 24;
    private const long MaxBackgroundAutoOcrImageBytes = 8L * 1024L * 1024L;
    private static readonly TimeSpan CaptureDebounceDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(350);

    private readonly ClipboardStore _store;
    private readonly SettingsService _settingsService;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _storeLock = new(1, 1);
    private readonly object _foregroundTrackingGate = new();
    private readonly HashSet<object> _activeForegroundTrackingViews = [];
    private readonly object _pasteQueueGate = new();
    private readonly List<string> _pasteQueueIds = [];
    private readonly SemaphoreSlim _externalPasteAdvanceLock = new(1, 1);
    private readonly ClipboardKeyboardChordObserver _chordObserver;

    private ClipboardPlatformInterop.ListenerSession? _listenerSession;
    private CancellationTokenSource? _captureDebounceCts;
    private CancellationTokenSource? _saveDebounceCts;
    private ClipboardStoreDocument? _cachedDocument;
    private volatile IReadOnlyList<ClipboardHistoryItem> _itemsSnapshot = Array.Empty<ClipboardHistoryItem>();
    private volatile IReadOnlyList<ClipboardObservedApp> _observedAppsSnapshot = Array.Empty<ClipboardObservedApp>();
    private volatile string? _currentClipboardContentHash;
    private volatile string? _lastExternalCapturedItemId;
    private static readonly Lazy<HttpClient> s_safeTitleHttpClient = new(CreateSafeUrlTitleHttpClient);
    private int _capturePendingCount;
    private int _captureInFlight;
    private long _lastExternalCaptureSuccessTickMs;
    private long _lastExternalCaptureSkipTickMs;
    private bool _isMonitoring;
    private bool _isPaused;
    private bool _isPasteQueueActive;
    private bool _isPasteQueueLiveCapture;
    private bool _isDisposed;
    private readonly QuickCaptureService? _quickCaptureService;
    private bool _legacyRecentMigrationAttempted;

    public ClipboardHistoryService(
        ClipboardStore store,
        SettingsService settingsService,
        Action<string>? log = null,
        QuickCaptureService? quickCaptureService = null)
    {
        _store = store;
        _settingsService = settingsService;
        _log = log;
        _quickCaptureService = quickCaptureService;
        _chordObserver = new ClipboardKeyboardChordObserver(OnDoubleCopyChordObserved, OnExternalPasteChordObserved);
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public event EventHandler? HistoryChanged;

    public event EventHandler? PauseStateChanged;

    public event EventHandler? PasteQueueChanged;

    public event EventHandler<ClipboardDoubleCopyEventArgs>? DoubleCopyActionExecuted;

    public event EventHandler? ItemsChanged
    {
        add => HistoryChanged += value;
        remove => HistoryChanged -= value;
    }

    public event EventHandler? MonitoringStateChanged
    {
        add => PauseStateChanged += value;
        remove => PauseStateChanged -= value;
    }

    public IReadOnlyList<ClipboardHistoryItem> Items => _itemsSnapshot;

    public string? CurrentClipboardContentHash => _currentClipboardContentHash;

    public IReadOnlyList<ClipboardObservedApp> ObservedApps => _observedAppsSnapshot;

    public bool IsMonitoring => _isMonitoring;

    public bool IsPaused => _isPaused;

    public bool IsPasteQueueActive
    {
        get
        {
            lock (_pasteQueueGate)
            {
                return _isPasteQueueActive;
            }
        }
    }

    public bool IsPasteQueueLiveCapture
    {
        get
        {
            lock (_pasteQueueGate)
            {
                return _isPasteQueueActive && _isPasteQueueLiveCapture;
            }
        }
    }

    public int PasteQueueCount
    {
        get
        {
            lock (_pasteQueueGate)
            {
                return _pasteQueueIds.Count;
            }
        }
    }

    internal IntPtr LastExternalForegroundWindow =>
        _listenerSession?.LastExternalForegroundWindow ?? IntPtr.Zero;

    public string ImageDirectory => _store.ImageDirectory;

    public string ThumbnailDirectory => _store.ThumbnailDirectory;

    public string PayloadDirectory => _store.PayloadDirectory;

    public string ExportDirectory => _store.ExportDirectory;

    internal bool IsStoreLoadedInMemory => _cachedDocument is not null;

    private bool IsForegroundTrackingWanted
    {
        get
        {
            lock (_foregroundTrackingGate)
            {
                return !_isDisposed && _activeForegroundTrackingViews.Count > 0;
            }
        }
    }

    public void SetViewForegroundTracking(object viewToken, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(viewToken);

        bool shouldTrack;
        lock (_foregroundTrackingGate)
        {
            if (isActive && !_isDisposed)
            {
                _activeForegroundTrackingViews.Add(viewToken);
            }
            else
            {
                _activeForegroundTrackingViews.Remove(viewToken);
            }

            shouldTrack = !_isDisposed && _activeForegroundTrackingViews.Count > 0;
        }

        UiDispatch.RunOrDefer(() => _listenerSession?.SetForegroundTrackingEnabled(shouldTrack));
    }

    public void CaptureExternalForegroundWindow()
    {
        if (_isDisposed)
        {
            return;
        }

        UiDispatch.RunOrDefer(() => _listenerSession?.CaptureCurrentExternalForeground());
    }

    public void StartMonitoring()
    {
        if (_isDisposed || _isMonitoring)
        {
            return;
        }

        _isMonitoring = true;
        ReconcileChordObserver();
        UiDispatch.RunOrDefer(() =>
        {
            if (_isDisposed || !_isMonitoring || _listenerSession is not null)
            {
                return;
            }

            try
            {
                _listenerSession = new ClipboardPlatformInterop.ListenerSession(OnNativeClipboardUpdate);
                if (IsForegroundTrackingWanted)
                {
                    _listenerSession.SetForegroundTrackingEnabled(true);
                }

                ClipboardStorageFileOps.CleanupOldFiles(_store.ExportDirectory, TimeSpan.FromDays(1));
                _log?.Invoke("[Clipboard] Started Win32 clipboard listener.");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Clipboard] Listener start failed: {ex.Message}");
            }
        });
    }

    public void StopMonitoring()
    {
        if (!_isMonitoring && _listenerSession is null)
        {
            return;
        }

        _isMonitoring = false;
        ReconcileChordObserver();
        CancelCaptureDebounce();
        UiDispatch.RunOrDefer(() =>
        {
            _listenerSession?.Dispose();
            _listenerSession = null;
            _log?.Invoke("[Clipboard] Stopped Win32 clipboard listener.");
        });
    }

    public void SetPaused(bool paused)
    {
        if (_isPaused == paused)
        {
            return;
        }

        _isPaused = paused;
        if (paused)
        {
            CancelCaptureDebounce();
        }

        ReconcileChordObserver();
        PauseStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task EnsureLoadedAsync()
    {
        _ = await GetItemsAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClipboardHistoryItem>> GetItemsAsync()
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            if (PruneExpiredAndExcessItemsLocked(doc))
            {
                ScheduleSave();
            }

            RefreshSnapshotsLocked(doc);
            return _itemsSnapshot;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public string? GetAbsolutePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.Combine(_store.RootDirectory, normalized);
        return File.Exists(candidate) ? candidate : null;
    }

    public Task<string?> PrepareDragExportImageAsync(ClipboardHistoryItem item) =>
        PrepareExportImagePathAsync(item);

    public string? ResolveImagePath(ClipboardHistoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ImageFileName))
        {
            return null;
        }

        string path = Path.Combine(_store.ImageDirectory, item.ImageFileName);
        return File.Exists(path) ? path : null;
    }

    public string? ResolveThumbnailPath(ClipboardHistoryItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ThumbnailFileName))
        {
            string thumbPath = Path.Combine(_store.ThumbnailDirectory, item.ThumbnailFileName);
            if (File.Exists(thumbPath))
            {
                return thumbPath;
            }
        }

        return ResolveImagePath(item);
    }

    public async Task<string?> PrepareExportImagePathAsync(ClipboardHistoryItem item)
    {
        string? sourcePath = ResolveImagePath(item);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        Directory.CreateDirectory(_store.ExportDirectory);
        DateTime timestamp = item.CreatedAtUtcTicks > 0
            ? new DateTime(item.CreatedAtUtcTicks, DateTimeKind.Utc).ToLocalTime()
            : DateTime.Now;
        string exportPath = Path.Combine(
            _store.ExportDirectory,
            $"DeskBox_Clip_{timestamp:yyyyMMdd_HHmmss}_{item.Id[..Math.Min(6, item.Id.Length)]}.png");

        if (!File.Exists(exportPath))
        {
            await Task.Run(() => ClipboardStorageFileOps.CopyFileOverwrite(sourcePath, exportPath)).ConfigureAwait(false);
        }

        return exportPath;
    }

    /// <summary>
    /// Ingests a pre-built item directly (used by unit tests and drag-and-drop imports).
    /// </summary>
    public async Task<ClipboardHistoryItem?> IngestItemAsync(
        ClipboardHistoryItem candidate,
        ClipboardObservedApp? sourceApp = null,
        bool fromExternalCapture = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            long nowTicks = DateTime.UtcNow.Ticks;

            if (sourceApp is not null && !string.IsNullOrWhiteSpace(sourceApp.AppPath))
            {
                RecordObservedAppLocked(doc, sourceApp, nowTicks);
            }

            ClipboardHistoryItem result;
            int existingIdx = doc.Items.FindIndex(x =>
                string.Equals(x.ContentHash, candidate.ContentHash, StringComparison.Ordinal));
            if (existingIdx >= 0)
            {
                ClipboardHistoryItem existing = doc.Items[existingIdx];
                existing.UpdatedAtUtcTicks = nowTicks;
                existing.UseCount = Math.Max(1, existing.UseCount + 1);
                if (!string.IsNullOrWhiteSpace(candidate.SourceAppName))
                {
                    existing.SourceAppName = candidate.SourceAppName;
                    existing.SourceAppPath = candidate.SourceAppPath;
                }

                if (string.IsNullOrWhiteSpace(existing.UrlTitle) && !string.IsNullOrWhiteSpace(candidate.UrlTitle))
                {
                    existing.UrlTitle = candidate.UrlTitle;
                }

                if (string.IsNullOrWhiteSpace(existing.CodeLanguage) && !string.IsNullOrWhiteSpace(candidate.CodeLanguage))
                {
                    existing.CodeLanguage = candidate.CodeLanguage;
                }

                doc.Items.RemoveAt(existingIdx);
                _ = await _store.ExternalizeItemPayloadsAsync(existing).ConfigureAwait(false);
                doc.Items.Insert(0, existing);
                result = existing;
            }
            else
            {
                if (candidate.CreatedAtUtcTicks <= 0)
                {
                    candidate.CreatedAtUtcTicks = nowTicks;
                }

                candidate.UpdatedAtUtcTicks = nowTicks;
                _ = await _store.ExternalizeItemPayloadsAsync(candidate).ConfigureAwait(false);
                doc.Items.Insert(0, candidate);
                result = candidate;
            }

            _currentClipboardContentHash = result.ContentHash;
            if (fromExternalCapture)
            {
                _lastExternalCapturedItemId = result.Id;
                Interlocked.Exchange(ref _lastExternalCaptureSuccessTickMs, Environment.TickCount64);
            }

            PruneExpiredAndExcessItemsLocked(doc);
            ScheduleSave();
            RaiseHistoryChanged();

            string? restageHeadId = null;
            bool queueChanged = false;
            lock (_pasteQueueGate)
            {
                if (_isPasteQueueActive && _isPasteQueueLiveCapture)
                {
                    if (fromExternalCapture || !_pasteQueueIds.Contains(result.Id, StringComparer.Ordinal))
                    {
                        _pasteQueueIds.Add(result.Id);
                        queueChanged = true;
                    }

                    if (_pasteQueueIds.Count >= 2 &&
                        !string.Equals(_pasteQueueIds[0], result.Id, StringComparison.Ordinal))
                    {
                        restageHeadId = _pasteQueueIds[0];
                    }
                }
            }

            if (queueChanged)
            {
                ReconcileChordObserver();
                RaisePasteQueueChanged();
            }

            if (_isMonitoring && !string.IsNullOrEmpty(restageHeadId))
            {
                ClipboardHistoryItem? headItem = doc.Items.FirstOrDefault(x => string.Equals(x.Id, restageHeadId, StringComparison.Ordinal));
                if (headItem is not null)
                {
                    _ = RestagePasteQueueHeadAfterDelayAsync(headItem);
                }
            }

            return result;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<bool> TogglePinAsync(string itemId)
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            ClipboardHistoryItem? item = doc.Items.FirstOrDefault(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
            if (item is null)
            {
                return false;
            }

            item.IsPinned = !item.IsPinned;
            ScheduleSave();
            RaiseHistoryChanged();
            return item.IsPinned;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<bool> UpdateItemNoteAsync(string itemId, string? note)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        string? normalizedNote = string.IsNullOrWhiteSpace(note)
            ? null
            : note.Trim();
        if (normalizedNote is { Length: > 120 })
        {
            normalizedNote = normalizedNote[..120];
        }

        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            ClipboardHistoryItem? item = doc.Items.FirstOrDefault(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
            if (item is null)
            {
                return false;
            }

            if (string.Equals(item.Note, normalizedNote, StringComparison.Ordinal))
            {
                return true;
            }

            item.Note = normalizedNote;
            ScheduleSave();
            RaiseHistoryChanged();
            return true;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<ClipboardHistoryItem?> UpdateItemTextAsync(string itemId, string newText)
    {
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(newText))
        {
            return null;
        }

        string trimmed = newText.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            ClipboardHistoryItem? item = doc.Items.FirstOrDefault(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
            if (item is null || item.Kind != ClipboardItemKind.Text)
            {
                return null;
            }

            ClipboardClassifier.ClassifyText(
                trimmed,
                htmlContent: null,
                rtfContent: null,
                out ClipboardTextSubKind subKind,
                out string? colorHex,
                out string? codeLanguage);

            bool isSecret = ClipboardClassifier.IsHighConfidenceSecret(trimmed);
            string normalizedKey = trimmed.Replace("\r\n", "\n", StringComparison.Ordinal);
            string contentHash = $"txt:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}";

            item.SubKind = subKind;
            item.ColorHex = colorHex;
            item.CodeLanguage = codeLanguage;
            item.UrlTitle = subKind == ClipboardTextSubKind.Url ? item.UrlTitle : null;
            item.ContentHash = contentHash;
            item.Summary = ClipboardClassifier.BuildSummary(trimmed);
            item.TextContent = newText;
            item.HtmlContent = null;
            item.RtfContent = null;
            item.TextPayloadFileName = null;
            item.HtmlPayloadFileName = null;
            item.RtfPayloadFileName = null;
            item.CharCount = newText.Length;
            item.LineCount = ClipboardHistoryItem.CountLines(newText);
            item.ByteSize = Encoding.UTF8.GetByteCount(newText);
            item.IsSensitive = isSecret;
            item.UpdatedAtUtcTicks = DateTime.UtcNow.Ticks;

            _ = await _store.ExternalizeItemPayloadsAsync(item).ConfigureAwait(false);
            CleanupOrphanedMediaLocked(doc);
            ScheduleSave();
            RaiseHistoryChanged();
            return item;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<bool> DeleteItemAsync(string itemId)
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            int removed = doc.Items.RemoveAll(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
            if (removed <= 0)
            {
                return false;
            }

            CleanupOrphanedMediaLocked(doc);
            await FlushSaveLockedAsync(doc).ConfigureAwait(false);
            RemoveFromPasteQueue([itemId]);
            RaiseHistoryChanged();
            return true;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<int> SetPinnedBatchAsync(IReadOnlyList<string> itemIds, bool isPinned)
    {
        if (itemIds is null || itemIds.Count == 0)
        {
            return 0;
        }

        var idSet = new HashSet<string>(itemIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
        if (idSet.Count == 0)
        {
            return 0;
        }

        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            int updated = 0;
            foreach (ClipboardHistoryItem item in doc.Items)
            {
                if (idSet.Contains(item.Id) && item.IsPinned != isPinned)
                {
                    item.IsPinned = isPinned;
                    updated++;
                }
            }

            if (updated > 0)
            {
                ScheduleSave();
                RaiseHistoryChanged();
            }

            return updated;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<int> DeleteItemsBatchAsync(IReadOnlyList<string> itemIds)
    {
        if (itemIds is null || itemIds.Count == 0)
        {
            return 0;
        }

        var idSet = new HashSet<string>(itemIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
        if (idSet.Count == 0)
        {
            return 0;
        }

        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            int removed = doc.Items.RemoveAll(x => idSet.Contains(x.Id));
            if (removed <= 0)
            {
                return 0;
            }

            CleanupOrphanedMediaLocked(doc);
            await FlushSaveLockedAsync(doc).ConfigureAwait(false);
            RemoveFromPasteQueue(idSet);
            RaiseHistoryChanged();
            return removed;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<int> ClearUnpinnedAsync()
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            var removedIds = doc.Items.Where(x => !x.IsPinned).Select(x => x.Id).ToList();
            int removed = doc.Items.RemoveAll(x => !x.IsPinned);
            CleanupOrphanedMediaLocked(doc);
            await FlushSaveLockedAsync(doc).ConfigureAwait(false);
            if (removedIds.Count > 0)
            {
                RemoveFromPasteQueue(removedIds);
            }

            RaiseHistoryChanged();
            return removed;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<int> ClearAllAsync()
    {
        StopPasteQueue();
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            int removed = doc.Items.Count;
            doc.Items.Clear();
            doc.ObservedApps.Clear();
            _currentClipboardContentHash = null;
            _lastExternalCapturedItemId = null;
            CleanupOrphanedMediaLocked(doc);
            await FlushSaveLockedAsync(doc).ConfigureAwait(false);
            RaiseHistoryChanged();
            return removed;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public string? ResolvePreviewableImagePath(ClipboardHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind == ClipboardItemKind.Image)
        {
            return ResolveImagePath(item);
        }

        if (item.Kind == ClipboardItemKind.Files &&
            item.FilePaths is { Count: 1 } &&
            ClipboardClassifier.IsPreviewableImageExtension(item.FilePaths[0]) &&
            File.Exists(item.FilePaths[0]))
        {
            return item.FilePaths[0];
        }

        return null;
    }

    public async Task<string?> ExtractAndCacheOcrTextAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        IReadOnlyList<ClipboardHistoryItem> items = _itemsSnapshot.Count > 0
            ? _itemsSnapshot
            : await GetItemsAsync().ConfigureAwait(false);
        ClipboardHistoryItem? snapshotItem = items.FirstOrDefault(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
        if (snapshotItem is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(snapshotItem.OcrText))
        {
            return snapshotItem.OcrText;
        }

        string? imagePath = ResolvePreviewableImagePath(snapshotItem);
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        string? extracted = await ClipboardOcrService.ExtractTextFromImageFileAsync(imagePath, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(extracted))
        {
            return null;
        }

        await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            ClipboardHistoryItem? target = doc.Items.FirstOrDefault(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
            if (target is not null && !string.Equals(target.OcrText, extracted, StringComparison.Ordinal))
            {
                target.OcrText = extracted;
                ScheduleSave();
                RaiseHistoryChanged();
            }
        }
        finally
        {
            _storeLock.Release();
        }

        return extracted;
    }

    public async Task<bool> CopyGeneratedTextToClipboardAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return await UiDispatch.RunAsync(() =>
        {
            try
            {
                DeskBoxClipboardWriteScope.MarkText(text);
                var pkg = new DataPackage();
                pkg.SetText(text);
                Clipboard.SetContent(pkg);
                TryFlushClipboard();
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Clipboard] CopyGeneratedText failed: {ex.Message}");
                return Task.FromResult(false);
            }
        }).ConfigureAwait(false);
    }

    public IReadOnlyList<ClipboardHistoryItem> GetPasteQueueSnapshot()
    {
        string[] ids;
        lock (_pasteQueueGate)
        {
            if (!_isPasteQueueActive || _pasteQueueIds.Count == 0)
            {
                return Array.Empty<ClipboardHistoryItem>();
            }

            ids = _pasteQueueIds.ToArray();
        }

        IReadOnlyList<ClipboardHistoryItem> snapshot = _itemsSnapshot;
        var map = new Dictionary<string, ClipboardHistoryItem>(snapshot.Count, StringComparer.Ordinal);
        foreach (ClipboardHistoryItem item in snapshot)
        {
            map[item.Id] = item;
        }

        var list = new List<ClipboardHistoryItem>(ids.Length);
        foreach (string id in ids)
        {
            if (map.TryGetValue(id, out ClipboardHistoryItem? found))
            {
                list.Add(found);
            }
        }

        return list;
    }

    public void StartLivePasteQueue()
    {
        lock (_pasteQueueGate)
        {
            _isPasteQueueActive = true;
            _isPasteQueueLiveCapture = true;
        }

        ReconcileChordObserver();
        RaisePasteQueueChanged();
    }

    public void StopPasteQueue()
    {
        bool changed = false;
        lock (_pasteQueueGate)
        {
            if (_isPasteQueueActive || _pasteQueueIds.Count > 0 || _isPasteQueueLiveCapture)
            {
                _isPasteQueueActive = false;
                _isPasteQueueLiveCapture = false;
                _pasteQueueIds.Clear();
                changed = true;
            }
        }

        if (changed)
        {
            ReconcileChordObserver();
            RaisePasteQueueChanged();
        }
    }

    public async Task<int> EnqueuePasteQueueAsync(IReadOnlyList<string> itemIds, bool enableLiveCapture = false)
    {
        if (itemIds is null || itemIds.Count == 0)
        {
            if (enableLiveCapture)
            {
                StartLivePasteQueue();
            }

            return 0;
        }

        IReadOnlyList<ClipboardHistoryItem> items = _itemsSnapshot.Count > 0
            ? _itemsSnapshot
            : await GetItemsAsync().ConfigureAwait(false);
        var validIds = new HashSet<string>(items.Select(x => x.Id), StringComparer.Ordinal);

        int added = 0;
        string? headId = null;
        bool wasEmpty;
        lock (_pasteQueueGate)
        {
            wasEmpty = _pasteQueueIds.Count == 0;
            foreach (string id in itemIds)
            {
                if (!string.IsNullOrWhiteSpace(id) &&
                    validIds.Contains(id) &&
                    !_pasteQueueIds.Contains(id, StringComparer.Ordinal))
                {
                    _pasteQueueIds.Add(id);
                    added++;
                }
            }

            if (_pasteQueueIds.Count > 0)
            {
                _isPasteQueueActive = true;
                if (enableLiveCapture)
                {
                    _isPasteQueueLiveCapture = true;
                }

                headId = _pasteQueueIds[0];
            }
        }

        if (added > 0 || enableLiveCapture)
        {
            ReconcileChordObserver();
            RaisePasteQueueChanged();
        }

        if (_isMonitoring && wasEmpty && !string.IsNullOrEmpty(headId))
        {
            ClipboardHistoryItem? head = items.FirstOrDefault(x => string.Equals(x.Id, headId, StringComparison.Ordinal));
            if (head is not null)
            {
                _ = await CopyItemToClipboardAsync(head, forcePlainText: false, recordReuse: false).ConfigureAwait(false);
            }
        }

        return added;
    }

    public async Task<bool> SkipPasteQueueHeadAsync()
    {
        return await AdvancePasteQueueInternalAsync(stageNextToClipboard: true).ConfigureAwait(false);
    }

    public async Task<(bool Copied, bool Pasted)> PasteNextFromQueueAsync(bool forcePlainText = false)
    {
        string? headId;
        lock (_pasteQueueGate)
        {
            headId = _isPasteQueueActive && _pasteQueueIds.Count > 0 ? _pasteQueueIds[0] : null;
        }

        if (string.IsNullOrEmpty(headId))
        {
            return (false, false);
        }

        (bool copied, bool pasted) = await PasteItemToExternalWindowAsync(headId, forcePlainText).ConfigureAwait(false);
        if (copied)
        {
            await Task.Delay(65).ConfigureAwait(false);
            await AdvancePasteQueueInternalAsync(stageNextToClipboard: true).ConfigureAwait(false);
        }

        return (copied, pasted);
    }

    internal async Task<bool> AdvancePasteQueueForTestingAsync(bool stageNextToClipboard = false) =>
        await AdvancePasteQueueInternalAsync(stageNextToClipboard).ConfigureAwait(false);

    private async Task<bool> AdvancePasteQueueInternalAsync(bool stageNextToClipboard)
    {
        string? nextHeadId = null;
        bool advanced = false;
        lock (_pasteQueueGate)
        {
            if (_isPasteQueueActive && _pasteQueueIds.Count > 0)
            {
                _pasteQueueIds.RemoveAt(0);
                advanced = true;
                if (_pasteQueueIds.Count > 0)
                {
                    nextHeadId = _pasteQueueIds[0];
                }
                else
                {
                    _isPasteQueueActive = false;
                    _isPasteQueueLiveCapture = false;
                }
            }
        }

        if (!advanced)
        {
            return false;
        }

        ReconcileChordObserver();
        RaisePasteQueueChanged();

        if (_isMonitoring && stageNextToClipboard && !string.IsNullOrEmpty(nextHeadId))
        {
            IReadOnlyList<ClipboardHistoryItem> items = _itemsSnapshot.Count > 0
                ? _itemsSnapshot
                : await GetItemsAsync().ConfigureAwait(false);
            ClipboardHistoryItem? nextItem = items.FirstOrDefault(x => string.Equals(x.Id, nextHeadId, StringComparison.Ordinal));
            if (nextItem is not null)
            {
                _ = await CopyItemToClipboardAsync(nextItem, forcePlainText: false, recordReuse: false).ConfigureAwait(false);
            }
        }

        return true;
    }

    private void RemoveFromPasteQueue(IEnumerable<string> removedIds)
    {
        var removeSet = removedIds as HashSet<string> ?? new HashSet<string>(removedIds, StringComparer.Ordinal);
        if (removeSet.Count == 0)
        {
            return;
        }

        bool changed = false;
        lock (_pasteQueueGate)
        {
            int removed = _pasteQueueIds.RemoveAll(id => removeSet.Contains(id));
            if (removed > 0)
            {
                changed = true;
                if (_pasteQueueIds.Count == 0 && !_isPasteQueueLiveCapture)
                {
                    _isPasteQueueActive = false;
                }
            }
        }

        if (changed)
        {
            ReconcileChordObserver();
            RaisePasteQueueChanged();
        }
    }

    private void RaisePasteQueueChanged()
    {
        PasteQueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string> GetFullTextAsync(
        ClipboardHistoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!string.IsNullOrWhiteSpace(item.TextPayloadFileName))
        {
            string? external = await _store.ReadPayloadTextAsync(item.TextPayloadFileName, cancellationToken)
                .ConfigureAwait(false);
            if (external is not null)
            {
                return external;
            }
        }

        return item.TextContent ?? item.Summary ?? string.Empty;
    }

    public async Task<(string Text, bool WasTruncated)> GetTextPreviewAsync(
        ClipboardHistoryItem item,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (maxChars <= 0)
        {
            return (string.Empty, false);
        }

        if (!string.IsNullOrWhiteSpace(item.TextPayloadFileName))
        {
            (string? preview, bool truncated) = await _store
                .ReadPayloadTextPreviewAsync(item.TextPayloadFileName, maxChars, cancellationToken)
                .ConfigureAwait(false);
            if (preview is not null)
            {
                return (preview, truncated);
            }
        }

        string inline = item.TextContent ?? item.Summary ?? string.Empty;
        if (inline.Length <= maxChars)
        {
            return (inline, false);
        }

        return (ClipboardStore.TruncateSurrogateSafe(inline, maxChars), true);
    }

    public async Task<string?> GetHtmlPayloadAsync(
        ClipboardHistoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!string.IsNullOrWhiteSpace(item.HtmlPayloadFileName))
        {
            string? external = await _store.ReadPayloadTextAsync(item.HtmlPayloadFileName, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(external))
            {
                return external;
            }
        }

        return item.HtmlContent;
    }

    public async Task<string?> GetRtfPayloadAsync(
        ClipboardHistoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!string.IsNullOrWhiteSpace(item.RtfPayloadFileName))
        {
            string? external = await _store.ReadPayloadTextAsync(item.RtfPayloadFileName, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(external))
            {
                return external;
            }
        }

        return item.RtfContent;
    }

    public async Task<IReadOnlyList<ClipboardObservedApp>> GetObservedAppsSnapshotAsync()
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedDocument is not null)
            {
                return _observedAppsSnapshot;
            }
        }
        finally
        {
            _storeLock.Release();
        }

        ClipboardStorageStats stats = await ClipboardStore.CalculateDiskStorageStatsAsync(_store.RootDirectory)
            .ConfigureAwait(false);
        return stats.ObservedApps;
    }

    public async Task<ClipboardStorageStats> GetStorageStatsAsync()
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedDocument is not null)
            {
                int activeCount = _cachedDocument.Items.Count;
                int pinnedCount = _cachedDocument.Items.Count(x => x.IsPinned);
                (int imgCount, long imgBytes) = ClipboardStorageFileOps.MeasureDirectoryFiles(_store.ImageDirectory);
                (_, long thumbBytes) = ClipboardStorageFileOps.MeasureDirectoryFiles(_store.ThumbnailDirectory);
                (_, long payloadBytes) = ClipboardStorageFileOps.MeasureDirectoryFiles(_store.PayloadDirectory);
                var observed = _cachedDocument.ObservedApps
                    .OrderByDescending(a => a.LastSeenUtcTicks)
                    .Take(MaxObservedApps)
                    .ToList();
                return new ClipboardStorageStats(
                    activeCount,
                    pinnedCount,
                    imgCount,
                    imgBytes + thumbBytes + payloadBytes,
                    observed);
            }
        }
        finally
        {
            _storeLock.Release();
        }

        return await ClipboardStore.CalculateDiskStorageStatsAsync(_store.RootDirectory)
            .ConfigureAwait(false);
    }

    public async Task<bool> CopyItemToClipboardAsync(ClipboardHistoryItem item, bool forcePlainText = false, bool recordReuse = true)
    {
        ArgumentNullException.ThrowIfNull(item);

        bool plain = forcePlainText || _settingsService.Settings.Clipboard.ClipboardDefaultPastePlainText;
        DeskBoxClipboardWriteScope.MarkContentHash(item.ContentHash);

        string? preloadedText = null;
        string? preloadedHtml = null;
        string? preloadedRtf = null;
        if (item.Kind == ClipboardItemKind.Text)
        {
            preloadedText = await GetFullTextAsync(item).ConfigureAwait(false);
            if (!plain)
            {
                preloadedHtml = await GetHtmlPayloadAsync(item).ConfigureAwait(false);
                preloadedRtf = await GetRtfPayloadAsync(item).ConfigureAwait(false);
            }
        }

        bool copied = await UiDispatch.RunAsync(async () =>
        {
            try
            {
                switch (item.Kind)
                {
                    case ClipboardItemKind.Files:
                    {
                        IReadOnlyList<string> paths = item.FilePaths ?? [];
                        if (plain)
                        {
                            string joined = string.Join(Environment.NewLine, paths);
                            DeskBoxClipboardWriteScope.MarkText(joined);
                            DeskBoxClipboardWriteScope.MarkContentHash(item.ContentHash);
                            var pkg = new DataPackage();
                            pkg.SetText(joined);
                            Clipboard.SetContent(pkg);
                            TryFlushClipboard();
                            return true;
                        }

                        string[] existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
                        if (existing.Length == 0)
                        {
                            return false;
                        }

                        DeskBoxClipboardWriteScope.MarkWrite(paths: existing);
                        DeskBoxClipboardWriteScope.MarkContentHash(item.ContentHash);
                        return ShellClipboardHelper.TrySetFileDropList(existing, cut: false);
                    }

                    case ClipboardItemKind.Image:
                    {
                        string? imgPath = ResolveImagePath(item);
                        if (string.IsNullOrWhiteSpace(imgPath))
                        {
                            return false;
                        }

                        StorageFile file = await StorageFile.GetFileFromPathAsync(imgPath);
                        var pkg = new DataPackage();
                        pkg.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));

                        string? exportPath = await PrepareExportImagePathAsync(item);
                        if (!string.IsNullOrWhiteSpace(exportPath) && File.Exists(exportPath))
                        {
                            StorageFile exportFile = await StorageFile.GetFileFromPathAsync(exportPath);
                            pkg.SetStorageItems([exportFile]);
                            DeskBoxClipboardWriteScope.MarkWrite(hasImage: true, paths: [exportPath]);
                        }
                        else
                        {
                            DeskBoxClipboardWriteScope.MarkWrite(hasImage: true);
                        }

                        DeskBoxClipboardWriteScope.MarkContentHash(item.ContentHash);
                        Clipboard.SetContent(pkg);
                        TryFlushClipboard();
                        return true;
                    }

                    default:
                    {
                        string text = preloadedText ?? item.TextContent ?? item.Summary;
                        DeskBoxClipboardWriteScope.MarkText(text);
                        DeskBoxClipboardWriteScope.MarkContentHash(item.ContentHash);
                        var pkg = new DataPackage();
                        pkg.SetText(text);
                        if (!plain)
                        {
                            if (!string.IsNullOrWhiteSpace(preloadedHtml))
                            {
                                pkg.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(preloadedHtml));
                            }

                            if (!string.IsNullOrWhiteSpace(preloadedRtf))
                            {
                                pkg.SetRtf(preloadedRtf);
                            }

                            if (item.SubKind == ClipboardTextSubKind.Url &&
                                Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri))
                            {
                                pkg.SetWebLink(uri);
                            }
                        }

                        Clipboard.SetContent(pkg);
                        TryFlushClipboard();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Clipboard] CopyItemToClipboard failed: {ex.Message}");
                return false;
            }
        }).ConfigureAwait(false);

        if (copied)
        {
            bool hashChanged = !string.Equals(_currentClipboardContentHash, item.ContentHash, StringComparison.Ordinal);
            _currentClipboardContentHash = item.ContentHash;
            if (recordReuse)
            {
                await RecordItemReuseAsync(item.Id, forceNotifyChanged: hashChanged).ConfigureAwait(false);
            }
            else if (hashChanged)
            {
                RaiseHistoryChanged();
            }
        }

        return copied;
    }

    public async Task<bool> CopyItemToClipboardAsync(string itemId, bool forcePlainText = false)
    {
        IReadOnlyList<ClipboardHistoryItem> items = _itemsSnapshot.Count > 0
            ? _itemsSnapshot
            : await GetItemsAsync().ConfigureAwait(false);
        ClipboardHistoryItem? item = items.FirstOrDefault(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
        return item is not null && await CopyItemToClipboardAsync(item, forcePlainText).ConfigureAwait(false);
    }

    public async Task<(bool Copied, bool Pasted)> PasteItemToExternalWindowAsync(string itemId, bool forcePlainText = false)
    {
        IReadOnlyList<ClipboardHistoryItem> items = _itemsSnapshot.Count > 0
            ? _itemsSnapshot
            : await GetItemsAsync().ConfigureAwait(false);
        ClipboardHistoryItem? item = items.FirstOrDefault(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            return (false, false);
        }

        (bool copied, ClipboardPasteOutcome outcome) = await PasteItemToActiveWindowAsync(item, forcePlainText).ConfigureAwait(false);
        return (copied, copied && outcome == ClipboardPasteOutcome.Pasted);
    }

    internal async Task<(bool Copied, ClipboardPasteOutcome Outcome)> PasteItemToActiveWindowAsync(
        ClipboardHistoryItem item,
        bool forcePlainText = false)
    {
        bool copied = await CopyItemToClipboardAsync(item, forcePlainText, recordReuse: true).ConfigureAwait(false);
        if (!copied)
        {
            return (false, ClipboardPasteOutcome.CopiedFallback);
        }

        IntPtr fallbackHwnd = _listenerSession?.LastExternalForegroundWindow ?? IntPtr.Zero;
        await Task.Delay(18).ConfigureAwait(false);
        ClipboardPasteOutcome outcome = await Task.Run(() =>
            ClipboardPlatformInterop.TryPasteToActiveOrLastWindow(fallbackHwnd)).ConfigureAwait(false);
        return (true, outcome);
    }

    internal async Task<int> ImportDroppedFilesAsync(IReadOnlyList<DroppedFilePath> files)
    {
        if (files.Count == 0)
        {
            return 0;
        }

        string durableRoot = Path.Combine(ExportDirectory, "Dropped");
        IReadOnlyList<string> durablePaths = await Task.Run(() =>
            DropStagingFileOps.PersistTemporaryDroppedFiles(files, durableRoot)).ConfigureAwait(false);
        return await ImportDroppedFilesAsync((IEnumerable<string>)durablePaths).ConfigureAwait(false);
    }

    public async Task<int> ImportDroppedFilesAsync(IEnumerable<string> paths)
    {
        string[] valid = paths
            .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (valid.Length == 0)
        {
            return 0;
        }

        ClipboardHistoryItem item = BuildFilesHistoryItem(valid, sourceApp: null);
        await IngestItemAsync(item).ConfigureAwait(false);
        await CopyItemToClipboardAsync(item, forcePlainText: false, recordReuse: false).ConfigureAwait(false);
        return valid.Length;
    }

    public async Task<bool> ImportDroppedTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ClipboardHistoryItem? item = BuildTextHistoryItem(
            text,
            htmlContent: null,
            rtfContent: null,
            sourceApp: null,
            _settingsService.Settings.Clipboard);
        if (item is null)
        {
            return false;
        }

        await IngestItemAsync(item).ConfigureAwait(false);
        await CopyItemToClipboardAsync(item, forcePlainText: false, recordReuse: false).ConfigureAwait(false);
        return true;
    }

    internal Task RecordItemReuseForTestingAsync(string itemId, bool forceNotifyChanged = false) =>
        RecordItemReuseAsync(itemId, forceNotifyChanged);

    private async Task RecordItemReuseAsync(string itemId, bool forceNotifyChanged = false)
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            int idx = doc.Items.FindIndex(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
            if (idx < 0)
            {
                return;
            }

            ClipboardHistoryItem item = doc.Items[idx];
            item.UseCount = Math.Max(1, item.UseCount + 1);
            bool moveToTop = _settingsService.Settings.Clipboard.ClipboardMoveToTopOnReuse;

            if (moveToTop)
            {
                item.UpdatedAtUtcTicks = DateTime.UtcNow.Ticks;
                if (idx > 0)
                {
                    doc.Items.RemoveAt(idx);
                    doc.Items.Insert(0, item);
                    RaiseHistoryChanged();
                }
                else if (forceNotifyChanged)
                {
                    RaiseHistoryChanged();
                }
            }
            else if (forceNotifyChanged)
            {
                RaiseHistoryChanged();
            }

            ScheduleSave();
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private void OnNativeClipboardUpdate()
    {
        if (_isDisposed || !_isMonitoring || _isPaused)
        {
            return;
        }

        if (!FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Clipboard))
        {
            return;
        }

        CancelCaptureDebounce();
        var cts = new CancellationTokenSource();
        _captureDebounceCts = cts;
        Interlocked.Increment(ref _capturePendingCount);
        _ = DebouncedCaptureAsync(cts.Token);
    }

    private async Task DebouncedCaptureAsync(CancellationToken token)
    {
        try
        {
            try
            {
                await Task.Delay(CaptureDebounceDelay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested || _isDisposed || !_isMonitoring || _isPaused)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0)
            {
                return;
            }

            try
            {
                await UiDispatch.RunAsync(() => CaptureCurrentClipboardOnUiThreadAsync(token)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Clipboard] Capture failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _captureInFlight, 0);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _capturePendingCount);
        }
    }

    private async Task CaptureCurrentClipboardOnUiThreadAsync(CancellationToken token)
    {
        ClipboardSettingsSlice slice = _settingsService.Settings.Clipboard;
        if (!slice.ClipboardCaptureText &&
            !slice.ClipboardCaptureImages &&
            !slice.ClipboardCaptureFiles)
        {
            return;
        }

        IntPtr fallbackHwnd = _listenerSession?.LastExternalForegroundWindow ?? IntPtr.Zero;
        ClipboardSourceAppInfo? rawSourceApp = ClipboardPlatformInterop.DetectSourceApp(fallbackHwnd);
        if (rawSourceApp is { } detectedApp && IsExcludedApp(detectedApp, slice.ClipboardExcludedApps))
        {
            Interlocked.Exchange(ref _lastExternalCaptureSkipTickMs, Environment.TickCount64);
            return;
        }

        ClipboardSourceAppInfo? effectiveSourceApp = slice.ClipboardShowSourceApp ? rawSourceApp : null;
        ClipboardObservedApp? observedApp = rawSourceApp is { } obs
            ? new ClipboardObservedApp
            {
                AppPath = obs.ExecutablePath,
                AppName = obs.DisplayName,
                LastSeenUtcTicks = DateTime.UtcNow.Ticks
            }
            : null;

        long maxImageBytes = Math.Max(1L, slice.ClipboardMaxImageSizeMb) * 1024L * 1024L;
        long maxTextBytes = Math.Max(1L, slice.ClipboardMaxTextSizeMb) * 1024L * 1024L;

        bool ignoredByPrivacyOrCut = false;
        bool hasFilesFormat = false;
        bool hasNonWhitespaceText = false;
        string? unicodeText = null;
        bool unicodeTextExceededLimit = false;
        byte[]? rawPngBytes = null;
        int rawPngWidth = 0;
        int rawPngHeight = 0;
        bool hasDibFormat = false;
        string? htmlFragment = null;
        string? rtfText = null;

        bool snapshotOpened = await RunClipboardAttemptWithAsyncRetryAsync(
            () => ClipboardPlatformInterop.TryReadNativeSnapshot(
                slice.ClipboardIgnoreSystemPrivacyFormats,
                slice.ClipboardCaptureFiles,
                slice.ClipboardCaptureImages,
                maxImageBytes,
                slice.ClipboardCaptureText,
                maxTextBytes,
                out ignoredByPrivacyOrCut,
                out hasFilesFormat,
                out hasNonWhitespaceText,
                out unicodeText,
                out unicodeTextExceededLimit,
                out rawPngBytes,
                out rawPngWidth,
                out rawPngHeight,
                out hasDibFormat,
                out htmlFragment,
                out rtfText),
            token).ConfigureAwait(true);

        if (!snapshotOpened || ignoredByPrivacyOrCut || token.IsCancellationRequested)
        {
            if (ignoredByPrivacyOrCut)
            {
                Interlocked.Exchange(ref _lastExternalCaptureSkipTickMs, Environment.TickCount64);
            }

            return;
        }

        // Priority 1: File drop list (CF_HDROP)
        if (hasFilesFormat)
        {
            if (slice.ClipboardCaptureFiles &&
                ShellClipboardHelper.TryGetFileDropList(out string[] droppedFiles, out bool isCutDrop))
            {
                if (isCutDrop)
                {
                    Interlocked.Exchange(ref _lastExternalCaptureSkipTickMs, Environment.TickCount64);
                    return;
                }

                if (droppedFiles.Length > 0)
                {
                    if (DeskBoxClipboardWriteScope.ShouldIgnorePaths(droppedFiles) ||
                        IsInternalClipboardCacheDrop(droppedFiles))
                    {
                        return;
                    }

                    ClipboardHistoryItem filesItem = BuildFilesHistoryItem(droppedFiles, effectiveSourceApp);
                    if (!DeskBoxClipboardWriteScope.ShouldIgnoreContentHash(filesItem.ContentHash))
                    {
                        await IngestItemAsync(filesItem, observedApp, fromExternalCapture: true).ConfigureAwait(false);
                    }
                }
            }

            return;
        }

        DataPackageView? dataView = null;
        if (!hasNonWhitespaceText || (slice.ClipboardCaptureText && unicodeText is null && !unicodeTextExceededLimit))
        {
            try
            {
                dataView = Clipboard.GetContent();
            }
            catch
            {
                // Continue with native snapshot payloads if WinRT DataPackageView fails.
            }
        }

        if (!hasNonWhitespaceText &&
            !unicodeTextExceededLimit &&
            dataView is not null &&
            SafeDataViewContains(dataView, StandardDataFormats.Text))
        {
            try
            {
                string? winrtText = await dataView.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(winrtText))
                {
                    hasNonWhitespaceText = true;
                    if (slice.ClipboardCaptureText)
                    {
                        unicodeText = winrtText;
                    }
                }
            }
            catch
            {
                // Ignore transient delayed-render failure and continue priority evaluation.
            }
        }

        bool hasWinRtBitmap = !hasNonWhitespaceText &&
                              dataView is not null &&
                              SafeDataViewContains(dataView, StandardDataFormats.Bitmap);
        bool hasAnyImagePayload = rawPngBytes is { Length: > 0 } || hasWinRtBitmap || hasDibFormat;

        ClipboardItemKind? priorityKind = ResolveCapturePriorityKind(
            hasFilesDrop: false,
            hasNonWhitespacePlainText: hasNonWhitespaceText,
            htmlFragment,
            rtfText,
            hasAnyImagePayload,
            out string? fallbackPlainTextFromRich);

        // Priority 2: Text / Link / Color / Rich Text (always beats composite spreadsheet/office bitmaps)
        if (priorityKind == ClipboardItemKind.Text)
        {
            if (!slice.ClipboardCaptureText || unicodeTextExceededLimit)
            {
                return;
            }

            string? text = !string.IsNullOrWhiteSpace(unicodeText)
                ? unicodeText
                : fallbackPlainTextFromRich;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            long utf8Bytes = Encoding.UTF8.GetByteCount(text);
            if (utf8Bytes > maxTextBytes || DeskBoxClipboardWriteScope.ShouldIgnoreText(text))
            {
                return;
            }

            ClipboardHistoryItem? textItem = BuildTextHistoryItem(
                text,
                htmlFragment,
                rtfText,
                effectiveSourceApp,
                slice);
            if (textItem is null)
            {
                Interlocked.Exchange(ref _lastExternalCaptureSkipTickMs, Environment.TickCount64);
                return;
            }

            if (!DeskBoxClipboardWriteScope.ShouldIgnoreContentHash(textItem.ContentHash))
            {
                ClipboardHistoryItem? ingested = await IngestItemAsync(textItem, observedApp, fromExternalCapture: true).ConfigureAwait(false);
                if (ingested is { SubKind: ClipboardTextSubKind.Url, IsSensitive: false } &&
                    string.IsNullOrWhiteSpace(ingested.UrlTitle))
                {
                    _ = TryResolveUrlTitleInBackgroundAsync(ingested.Id, text.Trim());
                }
            }

            return;
        }

        // Priority 3: Image / Screenshot (only when no files and no text/rich-text payload exist)
        if (priorityKind == ClipboardItemKind.Image &&
            slice.ClipboardCaptureImages &&
            !DeskBoxClipboardWriteScope.ShouldIgnoreImage())
        {
            await TryCaptureAndPersistImageOnUiThreadAsync(
                rawPngBytes,
                rawPngWidth,
                rawPngHeight,
                hasDibFormat,
                hasWinRtBitmap ? dataView : null,
                maxImageBytes,
                effectiveSourceApp,
                observedApp,
                token).ConfigureAwait(false);
        }
    }

    internal static ClipboardItemKind? ResolveCapturePriorityKind(
        bool hasFilesDrop,
        bool hasNonWhitespacePlainText,
        string? htmlFragment,
        string? rtfText,
        bool hasImagePayload,
        out string? fallbackPlainTextFromRich)
    {
        fallbackPlainTextFromRich = null;

        if (hasFilesDrop)
        {
            return ClipboardItemKind.Files;
        }

        if (hasNonWhitespacePlainText)
        {
            return ClipboardItemKind.Text;
        }

        if (TryExtractVisiblePlainTextFromHtml(htmlFragment, out string? htmlPlain) &&
            !string.IsNullOrWhiteSpace(htmlPlain))
        {
            fallbackPlainTextFromRich = htmlPlain;
            return ClipboardItemKind.Text;
        }

        if (TryExtractVisiblePlainTextFromRtf(rtfText, out string? rtfPlain) &&
            !string.IsNullOrWhiteSpace(rtfPlain))
        {
            fallbackPlainTextFromRich = rtfPlain;
            return ClipboardItemKind.Text;
        }

        if (hasImagePayload)
        {
            return ClipboardItemKind.Image;
        }

        return null;
    }

    internal bool IsInternalClipboardCacheDrop(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return false;
        }

        int validCount = 0;
        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            validCount++;
            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch
            {
                return false;
            }

            if (!IsPathInsideDirectory(full, _store.ExportDirectory) &&
                !IsPathInsideDirectory(full, _store.ImageDirectory) &&
                !IsPathInsideDirectory(full, _store.ThumbnailDirectory) &&
                !IsPathInsideDirectory(full, _store.PayloadDirectory))
            {
                return false;
            }
        }

        return validCount > 0;
    }

    private static bool IsPathInsideDirectory(string candidateFullPath, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        string normalizedDir = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidateFullPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SafeDataViewContains(DataPackageView dataView, string formatId)
    {
        try
        {
            return dataView.Contains(formatId);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> RunClipboardAttemptWithAsyncRetryAsync(
        Func<bool> attemptOnUiThread,
        CancellationToken token)
    {
        if (attemptOnUiThread())
        {
            return true;
        }

        int retryCount = ClipboardPlatformInterop.OpenRetryDelaysMs.Length;
        for (int i = 0; i < retryCount; i++)
        {
            int delayMs = ClipboardPlatformInterop.OpenRetryDelaysMs[i];
            await Task.Delay(delayMs, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return false;
            }

            if (attemptOnUiThread())
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryExtractVisiblePlainTextFromHtml(string? htmlPayload, out string? plainText)
    {
        plainText = null;
        if (string.IsNullOrWhiteSpace(htmlPayload))
        {
            return false;
        }

        ReadOnlySpan<char> span = htmlPayload.AsSpan();
        int startFrag = span.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        if (startFrag >= 0)
        {
            span = span[(startFrag + "<!--StartFragment-->".Length)..];
            int endFrag = span.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
            if (endFrag >= 0)
            {
                span = span[..endFrag];
            }
        }
        else
        {
            int bodyStart = span.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (bodyStart >= 0)
            {
                int bodyCloseBracket = span[bodyStart..].IndexOf('>');
                if (bodyCloseBracket >= 0)
                {
                    span = span[(bodyStart + bodyCloseBracket + 1)..];
                    int bodyEnd = span.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (bodyEnd >= 0)
                    {
                        span = span[..bodyEnd];
                    }
                }
            }
            else if (span.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
            {
                int firstTag = span.IndexOf('<');
                if (firstTag < 0)
                {
                    return false;
                }

                span = span[firstTag..];
            }
        }

        var sb = new StringBuilder(Math.Min(span.Length, 4096));
        int idx = 0;
        while (idx < span.Length)
        {
            if (span[idx] == '<')
            {
                int close = span[idx..].IndexOf('>');
                if (close < 0)
                {
                    break;
                }

                ReadOnlySpan<char> tag = span.Slice(idx + 1, close - 1).Trim();
                if (tag.StartsWith("script", StringComparison.OrdinalIgnoreCase))
                {
                    idx += close + 1;
                    int endScript = span[idx..].IndexOf("</script>", StringComparison.OrdinalIgnoreCase);
                    idx = endScript >= 0 ? idx + endScript + "</script>".Length : span.Length;
                    continue;
                }

                if (tag.StartsWith("style", StringComparison.OrdinalIgnoreCase))
                {
                    idx += close + 1;
                    int endStyle = span[idx..].IndexOf("</style>", StringComparison.OrdinalIgnoreCase);
                    idx = endStyle >= 0 ? idx + endStyle + "</style>".Length : span.Length;
                    continue;
                }

                if (tag.StartsWith("br", StringComparison.OrdinalIgnoreCase) ||
                    tag.StartsWith("/p", StringComparison.OrdinalIgnoreCase) ||
                    tag.StartsWith("/div", StringComparison.OrdinalIgnoreCase) ||
                    tag.StartsWith("/tr", StringComparison.OrdinalIgnoreCase) ||
                    tag.StartsWith("/li", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append('\n');
                }
                else if (tag.StartsWith("/td", StringComparison.OrdinalIgnoreCase) ||
                         tag.StartsWith("/th", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append('\t');
                }

                idx += close + 1;
                continue;
            }

            sb.Append(span[idx]);
            idx++;
        }

        if (sb.Length == 0)
        {
            return false;
        }

        string decoded = WebUtility.HtmlDecode(sb.ToString());
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return false;
        }

        plainText = decoded.Trim();
        return plainText.Length > 0;
    }

    internal static bool TryExtractVisiblePlainTextFromRtf(string? rtfPayload, out string? plainText)
    {
        plainText = null;
        if (string.IsNullOrWhiteSpace(rtfPayload))
        {
            return false;
        }

        ReadOnlySpan<char> span = rtfPayload.AsSpan().TrimStart();
        if (!span.StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sb = new StringBuilder(Math.Min(span.Length, 4096));
        int depth = 0;
        int skipGroupDepth = -1;
        int i = 0;

        while (i < span.Length)
        {
            char c = span[i];
            if (c == '{')
            {
                depth++;
                i++;
                if (skipGroupDepth < 0 && i < span.Length && span[i] == '\\')
                {
                    ReadOnlySpan<char> rest = span[(i + 1)..];
                    if (rest.StartsWith("*") ||
                        rest.StartsWith("fonttbl", StringComparison.OrdinalIgnoreCase) ||
                        rest.StartsWith("colortbl", StringComparison.OrdinalIgnoreCase) ||
                        rest.StartsWith("stylesheet", StringComparison.OrdinalIgnoreCase) ||
                        rest.StartsWith("info", StringComparison.OrdinalIgnoreCase) ||
                        rest.StartsWith("pict", StringComparison.OrdinalIgnoreCase) ||
                        rest.StartsWith("object", StringComparison.OrdinalIgnoreCase))
                    {
                        skipGroupDepth = depth;
                    }
                }

                continue;
            }

            if (c == '}')
            {
                if (depth == skipGroupDepth)
                {
                    skipGroupDepth = -1;
                }

                depth = Math.Max(0, depth - 1);
                i++;
                continue;
            }

            if (skipGroupDepth >= 0)
            {
                i++;
                continue;
            }

            if (c == '\\')
            {
                i++;
                if (i >= span.Length)
                {
                    break;
                }

                char next = span[i];
                if (next is '\\' or '{' or '}')
                {
                    sb.Append(next);
                    i++;
                    continue;
                }

                if (next == '\'')
                {
                    // \'hh hex-encoded byte
                    i = Math.Min(span.Length, i + 3);
                    sb.Append('?');
                    continue;
                }

                if (!char.IsLetter(next))
                {
                    i++;
                    continue;
                }

                int wordStart = i;
                while (i < span.Length && char.IsLetter(span[i]))
                {
                    i++;
                }

                ReadOnlySpan<char> word = span[wordStart..i];
                int numStart = i;
                if (i < span.Length && (span[i] == '-' || char.IsDigit(span[i])))
                {
                    i++;
                    while (i < span.Length && char.IsDigit(span[i]))
                    {
                        i++;
                    }
                }

                if (i < span.Length && span[i] == ' ')
                {
                    i++;
                }

                if (word.Equals("par", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("line", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append('\n');
                }
                else if (word.Equals("tab", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append('\t');
                }
                else if (word.Equals("u", StringComparison.OrdinalIgnoreCase) &&
                         numStart < span.Length &&
                         int.TryParse(span[numStart..(i > 0 && span[i - 1] == ' ' ? i - 1 : i)], out int codeUnit))
                {
                    ushort u16 = unchecked((ushort)(short)codeUnit);
                    if (u16 != 0)
                    {
                        sb.Append((char)u16);
                    }

                    // Skip single ANSI fallback char if present (commonly '?').
                    if (i < span.Length && span[i] == '?')
                    {
                        i++;
                    }
                }

                continue;
            }

            if (c is not '\r' and not '\n')
            {
                sb.Append(c);
            }

            i++;
        }

        string extracted = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(extracted))
        {
            return false;
        }

        plainText = extracted;
        return true;
    }

    private static ClipboardHistoryItem BuildFilesHistoryItem(
        IReadOnlyList<string> paths,
        ClipboardSourceAppInfo? sourceApp)
    {
        string[] cleaned = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToArray();
        string normalizedForHash = string.Join(
            '\n',
            cleaned.Select(p => p.TrimEnd('\\', '/').ToUpperInvariant()).OrderBy(p => p, StringComparer.Ordinal));
        string hash = $"files:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedForHash)))}";

        string firstName = Path.GetFileName(cleaned[0].TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(firstName))
        {
            firstName = cleaned[0];
        }

        string summary = cleaned.Length == 1
            ? firstName
            : $"{firstName} (+{cleaned.Length - 1})";

        long totalBytes = 0;
        foreach (string p in cleaned)
        {
            try
            {
                if (File.Exists(p))
                {
                    totalBytes += new FileInfo(p).Length;
                }
            }
            catch
            {
                // Ignore inaccessible file metadata.
            }
        }

        int imgW = 0;
        int imgH = 0;
        if (cleaned.Length == 1 && ClipboardClassifier.IsPreviewableImageExtension(cleaned[0]))
        {
            _ = ClipboardPlatformInterop.TryProbeImageFileDimensions(cleaned[0], out imgW, out imgH);
        }

        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            ContentHash = hash,
            Summary = summary,
            TextContent = string.Join(Environment.NewLine, cleaned),
            FilePaths = cleaned.ToList(),
            ImageWidth = imgW,
            ImageHeight = imgH,
            ByteSize = totalBytes,
            SourceAppName = sourceApp?.DisplayName,
            SourceAppPath = sourceApp?.ExecutablePath
        };
    }

    private static ClipboardHistoryItem? BuildTextHistoryItem(
        string rawText,
        string? htmlContent,
        string? rtfContent,
        ClipboardSourceAppInfo? sourceApp,
        ClipboardSettingsSlice slice)
    {
        string trimmed = rawText.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        bool isSecret = ClipboardClassifier.IsHighConfidenceSecret(trimmed);
        if (isSecret &&
            string.Equals(
                slice.ClipboardSensitiveProtectionMode,
                ClipboardSettingsSlice.SecretProtectionSkip,
                StringComparison.Ordinal))
        {
            return null;
        }

        ClipboardClassifier.ClassifyText(
            trimmed,
            htmlContent,
            rtfContent,
            out ClipboardTextSubKind subKind,
            out string? colorHex,
            out string? codeLanguage);

        string? urlTitle = null;
        if (subKind == ClipboardTextSubKind.Url && !isSecret)
        {
            _ = ClipboardClassifier.TryExtractHtmlAnchorTitle(htmlContent, trimmed, out urlTitle);
        }

        string normalizedKey = trimmed.Replace("\r\n", "\n", StringComparison.Ordinal);
        string contentHash = $"txt:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}";

        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            SubKind = subKind,
            ContentHash = contentHash,
            Summary = ClipboardClassifier.BuildSummary(trimmed),
            TextContent = rawText,
            HtmlContent = subKind == ClipboardTextSubKind.Html ? htmlContent : null,
            RtfContent = subKind is ClipboardTextSubKind.Html or ClipboardTextSubKind.Rtf ? rtfContent : null,
            ColorHex = colorHex,
            UrlTitle = urlTitle,
            CodeLanguage = codeLanguage,
            IsSensitive = isSecret,
            ByteSize = Encoding.UTF8.GetByteCount(rawText),
            SourceAppName = sourceApp?.DisplayName,
            SourceAppPath = sourceApp?.ExecutablePath
        };
    }

    private async Task TryResolveUrlTitleInBackgroundAsync(string itemId, string rawUrl)
    {
        if (_isDisposed || !Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || IsDisallowedHostName(uri.Host))
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2600));
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DeskBox/1.0");
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.1");

            using HttpResponseMessage resp = await s_safeTitleHttpClient.Value
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return;
            }

            string? mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrEmpty(mediaType) &&
                !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            byte[] buf = new byte[32768];
            int total = 0;
            while (total < buf.Length)
            {
                int r = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), cts.Token).ConfigureAwait(false);
                if (r <= 0)
                {
                    break;
                }

                total += r;
            }

            if (total <= 0)
            {
                return;
            }

            string snippet = Encoding.UTF8.GetString(buf, 0, total);
            if (!ClipboardClassifier.TryExtractHtmlDocumentTitle(snippet, out string? pageTitle) ||
                string.IsNullOrWhiteSpace(pageTitle))
            {
                return;
            }

            await _storeLock.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                if (_isDisposed || _cachedDocument is null)
                {
                    return;
                }

                ClipboardHistoryItem? target = _cachedDocument.Items.FirstOrDefault(
                    x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
                if (target is null || !string.IsNullOrWhiteSpace(target.UrlTitle))
                {
                    return;
                }

                target.UrlTitle = pageTitle;
                ScheduleSave();
                RaiseHistoryChanged();
            }
            finally
            {
                _storeLock.Release();
            }
        }
        catch
        {
            // Never surface transient network or non-HTML errors.
        }
    }

    private static HttpClient CreateSafeUrlTitleHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromMilliseconds(1800),
            ConnectCallback = static async (context, cancellationToken) =>
            {
                string host = context.DnsEndPoint.Host;
                if (IsDisallowedHostName(host))
                {
                    throw new InvalidOperationException("Loopback and private hosts are blocked.");
                }

                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                if (addresses.Length == 0 || addresses.Any(IsPrivateOrLoopbackAddress))
                {
                    throw new InvalidOperationException("Private IP address blocked.");
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    private static bool IsDisallowedHostName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out IPAddress? ip) && IsPrivateOrLoopbackAddress(ip);
    }

    private static bool IsPrivateOrLoopbackAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        byte[] bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork && bytes.Length == 4)
        {
            return bytes[0] == 0 ||
                   bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && bytes.Length == 16)
        {
            return ip.IsIPv6LinkLocal ||
                   ip.IsIPv6SiteLocal ||
                   (bytes[0] & 0xFE) == 0xFC;
        }

        return true;
    }

    private async Task TryCaptureAndPersistImageOnUiThreadAsync(
        byte[]? rawPngBytes,
        int rawPngWidth,
        int rawPngHeight,
        bool hasDibFormat,
        DataPackageView? dataViewWithBitmap,
        long maxImageBytes,
        ClipboardSourceAppInfo? effectiveSourceApp,
        ClipboardObservedApp? observedApp,
        CancellationToken token)
    {
        // Path 1: Native PNG buffer already captured without decoding.
        if (rawPngBytes is { Length: > 0 } && rawPngWidth > 0 && rawPngHeight > 0)
        {
            await PersistCapturedPngAndThumbnailAsync(
                rawPngBytes,
                rawPngWidth,
                rawPngHeight,
                preEncodedThumbBytes: rawPngWidth <= ThumbnailMaxPixelSize && rawPngHeight <= ThumbnailMaxPixelSize
                    ? rawPngBytes
                    : null,
                effectiveSourceApp,
                observedApp,
                token).ConfigureAwait(false);
            return;
        }

        // Path 2: WinRT StandardDataFormats.Bitmap (decoded ONCE for both full PNG and 320px thumbnail,
        // avoiding any 33 MB CF_DIB buffer copy when WinRT Bitmap is available).
        if (dataViewWithBitmap is not null)
        {
            try
            {
                IRandomAccessStreamReference bmpRef = await dataViewWithBitmap.GetBitmapAsync();
                using IRandomAccessStreamWithContentType stream = await bmpRef.OpenReadAsync();
                if (stream.Size > 0 && (long)stream.Size <= maxImageBytes * 4)
                {
                    var decoded = await DecodeOnceAndEncodeImageWithThumbnailAsync(stream, maxImageBytes, token)
                        .ConfigureAwait(true);
                    if (decoded.PngBytes is { Length: > 0 })
                    {
                        await PersistCapturedPngAndThumbnailAsync(
                            decoded.PngBytes,
                            decoded.Width,
                            decoded.Height,
                            decoded.ThumbBytes,
                            effectiveSourceApp,
                            observedApp,
                            token).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch
            {
                // Fall through to single-buffer CF_DIB fallback on UI thread.
            }
        }

        // Path 3: Single-buffer CF_DIB -> BMP fallback (WeChat / QQ / DingTalk / Snipaste),
        // decoded ONCE into SoftwareBitmap for both full PNG and 320px thumbnail.
        if (!hasDibFormat || token.IsCancellationRequested)
        {
            return;
        }

        byte[]? dibBmpBytes = null;
        bool dibOpened = await RunClipboardAttemptWithAsyncRetryAsync(
            () => ClipboardPlatformInterop.TryReadClipboardDibAsBmpOnce(maxImageBytes, out dibBmpBytes),
            token).ConfigureAwait(true);
        if (!dibOpened || dibBmpBytes is not { Length: > 0 } || token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            using var ms = new MemoryStream(dibBmpBytes, writable: false);
            using IRandomAccessStream winrtStream = ms.AsRandomAccessStream();
            var decoded = await DecodeOnceAndEncodeImageWithThumbnailAsync(winrtStream, maxImageBytes, token)
                .ConfigureAwait(false);
            if (decoded.PngBytes is { Length: > 0 })
            {
                await PersistCapturedPngAndThumbnailAsync(
                    decoded.PngBytes,
                    decoded.Width,
                    decoded.Height,
                    decoded.ThumbBytes,
                    effectiveSourceApp,
                    observedApp,
                    token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore malformed DIB buffers.
        }
    }

    private async Task PersistCapturedPngAndThumbnailAsync(
        byte[] pngBytes,
        int width,
        int height,
        byte[]? preEncodedThumbBytes,
        ClipboardSourceAppInfo? effectiveSourceApp,
        ClipboardObservedApp? observedApp,
        CancellationToken token)
    {
        if (pngBytes.Length == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        string sha256 = Convert.ToHexStringLower(SHA256.HashData(pngBytes));
        string contentHash = $"img:{sha256}";
        if (DeskBoxClipboardWriteScope.ShouldIgnoreContentHash(contentHash))
        {
            return;
        }

        string fileName = $"{sha256}.png";
        string imagePath = Path.Combine(_store.ImageDirectory, fileName);
        await ClipboardStorageFileOps.WriteBytesIfMissingAsync(imagePath, pngBytes, token).ConfigureAwait(false);

        string? thumbFileName;
        if (preEncodedThumbBytes is { Length: > 0 })
        {
            string thumbPath = Path.Combine(_store.ThumbnailDirectory, fileName);
            await ClipboardStorageFileOps.WriteBytesIfMissingAsync(thumbPath, preEncodedThumbBytes, token)
                .ConfigureAwait(false);
            thumbFileName = fileName;
        }
        else
        {
            thumbFileName = await EnsureThumbnailFromPngBytesAsync(pngBytes, width, height, fileName, token)
                .ConfigureAwait(false);
        }

        var imageItem = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            ContentHash = contentHash,
            Summary = $"{width} × {height}",
            ImageFileName = fileName,
            ThumbnailFileName = thumbFileName ?? fileName,
            ImageWidth = width,
            ImageHeight = height,
            ByteSize = pngBytes.LongLength,
            SourceAppName = effectiveSourceApp?.DisplayName,
            SourceAppPath = effectiveSourceApp?.ExecutablePath
        };

        ClipboardHistoryItem? ingested = await IngestItemAsync(imageItem, observedApp, fromExternalCapture: true).ConfigureAwait(false);
        if (ingested is not null &&
            pngBytes.LongLength <= MaxBackgroundAutoOcrImageBytes &&
            string.IsNullOrWhiteSpace(ingested.OcrText))
        {
            _ = TryPopulateImageOcrInBackgroundAsync(ingested.Id);
        }
    }

    private static async Task<(byte[]? PngBytes, byte[]? ThumbBytes, int Width, int Height)> DecodeOnceAndEncodeImageWithThumbnailAsync(
        IRandomAccessStream inputStream,
        long maxImageBytes,
        CancellationToken token)
    {
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(inputStream);
        uint pixelWidth = decoder.PixelWidth;
        uint pixelHeight = decoder.PixelHeight;
        if (pixelWidth == 0 || pixelHeight == 0)
        {
            return (null, null, 0, 0);
        }

        using SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);

        byte[]? fullPngBytes = await EncodeSoftwareBitmapToPngBytesAsync(
            softwareBitmap,
            scaledWidth: 0,
            scaledHeight: 0,
            maxImageBytes,
            token).ConfigureAwait(false);
        if (fullPngBytes is not { Length: > 0 })
        {
            return (null, null, 0, 0);
        }

        if (pixelWidth <= ThumbnailMaxPixelSize && pixelHeight <= ThumbnailMaxPixelSize)
        {
            return (fullPngBytes, fullPngBytes, (int)pixelWidth, (int)pixelHeight);
        }

        ComputeScaledDimensions(pixelWidth, pixelHeight, ThumbnailMaxPixelSize, out uint thumbW, out uint thumbH);
        byte[]? thumbPngBytes = await EncodeSoftwareBitmapToPngBytesAsync(
            softwareBitmap,
            thumbW,
            thumbH,
            maxImageBytes,
            token).ConfigureAwait(false);

        return (fullPngBytes, thumbPngBytes, (int)pixelWidth, (int)pixelHeight);
    }

    private static async Task<byte[]?> EncodeSoftwareBitmapToPngBytesAsync(
        SoftwareBitmap softwareBitmap,
        uint scaledWidth,
        uint scaledHeight,
        long maxBytes,
        CancellationToken token)
    {
        using var outMemory = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outMemory);
        encoder.SetSoftwareBitmap(softwareBitmap);
        if (scaledWidth > 0 && scaledHeight > 0)
        {
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            encoder.BitmapTransform.ScaledWidth = scaledWidth;
            encoder.BitmapTransform.ScaledHeight = scaledHeight;
        }

        await encoder.FlushAsync();
        if (outMemory.Size == 0 || (long)outMemory.Size > maxBytes)
        {
            return null;
        }

        var bytes = new byte[(int)outMemory.Size];
        outMemory.Seek(0);
        using Stream netStream = outMemory.AsStreamForRead();
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = await netStream.ReadAsync(bytes.AsMemory(offset, bytes.Length - offset), token)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            offset += read;
        }

        return bytes;
    }

    private static void ComputeScaledDimensions(
        uint pixelWidth,
        uint pixelHeight,
        uint maxSide,
        out uint scaledWidth,
        out uint scaledHeight)
    {
        if (pixelWidth >= pixelHeight)
        {
            scaledWidth = Math.Min(pixelWidth, maxSide);
            scaledHeight = Math.Max(1, (uint)Math.Round(pixelHeight * (scaledWidth / (double)pixelWidth)));
        }
        else
        {
            scaledHeight = Math.Min(pixelHeight, maxSide);
            scaledWidth = Math.Max(1, (uint)Math.Round(pixelWidth * (scaledHeight / (double)pixelHeight)));
        }
    }

    private async Task<string?> EnsureThumbnailFromPngBytesAsync(
        byte[] pngBytes,
        int knownWidth,
        int knownHeight,
        string fileName,
        CancellationToken token)
    {
        string thumbPath = Path.Combine(_store.ThumbnailDirectory, fileName);
        if (File.Exists(thumbPath))
        {
            return fileName;
        }

        if (knownWidth > 0 &&
            knownHeight > 0 &&
            knownWidth <= ThumbnailMaxPixelSize &&
            knownHeight <= ThumbnailMaxPixelSize)
        {
            await ClipboardStorageFileOps.WriteBytesIfMissingAsync(thumbPath, pngBytes, token).ConfigureAwait(false);
            return fileName;
        }

        try
        {
            using var srcMem = new MemoryStream(pngBytes, writable: false);
            using IRandomAccessStream srcStream = srcMem.AsRandomAccessStream();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(srcStream);
            if (decoder.PixelWidth <= ThumbnailMaxPixelSize && decoder.PixelHeight <= ThumbnailMaxPixelSize)
            {
                await ClipboardStorageFileOps.WriteBytesIfMissingAsync(thumbPath, pngBytes, token).ConfigureAwait(false);
                return fileName;
            }

            ComputeScaledDimensions(decoder.PixelWidth, decoder.PixelHeight, ThumbnailMaxPixelSize, out uint thumbW, out uint thumbH);
            var transform = new BitmapTransform
            {
                InterpolationMode = BitmapInterpolationMode.Fant,
                ScaledWidth = thumbW,
                ScaledHeight = thumbH
            };

            using SoftwareBitmap thumbBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            byte[]? thumbBytes = await EncodeSoftwareBitmapToPngBytesAsync(
                thumbBitmap,
                scaledWidth: 0,
                scaledHeight: 0,
                maxBytes: long.MaxValue,
                token).ConfigureAwait(false);
            if (thumbBytes is not { Length: > 0 })
            {
                return null;
            }

            await ClipboardStorageFileOps.WriteBytesIfMissingAsync(thumbPath, thumbBytes, token).ConfigureAwait(false);
            return fileName;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsExcludedApp(ClipboardSourceAppInfo sourceApp, IReadOnlyList<string>? excludedApps)
    {
        if (excludedApps is null || excludedApps.Count == 0)
        {
            return false;
        }

        string fullPath = sourceApp.ExecutablePath;
        string fileName = Path.GetFileName(fullPath);
        string stem = Path.GetFileNameWithoutExtension(fullPath);

        foreach (string rule in excludedApps)
        {
            if (string.IsNullOrWhiteSpace(rule))
            {
                continue;
            }

            string trimmed = rule.Trim();
            if (string.Equals(trimmed, fullPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, fileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, stem, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void RecordObservedAppLocked(ClipboardStoreDocument doc, ClipboardObservedApp app, long nowTicks)
    {
        int existingIdx = doc.ObservedApps.FindIndex(x =>
            string.Equals(x.AppPath, app.AppPath, StringComparison.OrdinalIgnoreCase));
        if (existingIdx >= 0)
        {
            ClipboardObservedApp existing = doc.ObservedApps[existingIdx];
            existing.AppName = app.AppName;
            existing.LastSeenUtcTicks = nowTicks;
            doc.ObservedApps.RemoveAt(existingIdx);
            doc.ObservedApps.Insert(0, existing);
        }
        else
        {
            app.LastSeenUtcTicks = nowTicks;
            doc.ObservedApps.Insert(0, app);
            if (doc.ObservedApps.Count > MaxObservedApps)
            {
                doc.ObservedApps.RemoveRange(MaxObservedApps, doc.ObservedApps.Count - MaxObservedApps);
            }
        }
    }

    private bool PruneExpiredAndExcessItemsLocked(ClipboardStoreDocument doc)
    {
        ClipboardSettingsSlice slice = _settingsService.Settings.Clipboard;
        int retentionDays = slice.ClipboardRetentionDays;
        int maxItems = Math.Max(10, slice.ClipboardMaxItems);
        bool removedAny = false;

        if (retentionDays > 0)
        {
            long cutoffTicks = DateTime.UtcNow.AddDays(-retentionDays).Ticks;
            int expiredRemoved = doc.Items.RemoveAll(item =>
                !item.IsPinned &&
                item.UpdatedAtUtcTicks > 0 &&
                item.UpdatedAtUtcTicks < cutoffTicks);
            if (expiredRemoved > 0)
            {
                removedAny = true;
            }
        }

        int unpinnedCount = doc.Items.Count(x => !x.IsPinned);
        if (unpinnedCount > maxItems)
        {
            int toRemove = unpinnedCount - maxItems;
            for (int i = doc.Items.Count - 1; i >= 0 && toRemove > 0; i--)
            {
                if (!doc.Items[i].IsPinned)
                {
                    doc.Items.RemoveAt(i);
                    toRemove--;
                    removedAny = true;
                }
            }
        }

        if (removedAny)
        {
            CleanupOrphanedMediaLocked(doc);
        }

        return removedAny;
    }

    private void CleanupOrphanedMediaLocked(ClipboardStoreDocument doc)
    {
        var referencedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedThumbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedPayloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ClipboardHistoryItem item in doc.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.ImageFileName))
            {
                referencedImages.Add(item.ImageFileName);
            }

            if (!string.IsNullOrWhiteSpace(item.ThumbnailFileName))
            {
                referencedThumbs.Add(item.ThumbnailFileName);
            }

            if (!string.IsNullOrWhiteSpace(item.TextPayloadFileName))
            {
                referencedPayloads.Add(item.TextPayloadFileName);
            }

            if (!string.IsNullOrWhiteSpace(item.HtmlPayloadFileName))
            {
                referencedPayloads.Add(item.HtmlPayloadFileName);
            }

            if (!string.IsNullOrWhiteSpace(item.RtfPayloadFileName))
            {
                referencedPayloads.Add(item.RtfPayloadFileName);
            }
        }

        ClipboardStorageFileOps.DeleteOrphanedFiles(_store.ImageDirectory, referencedImages);
        ClipboardStorageFileOps.DeleteOrphanedFiles(_store.ThumbnailDirectory, referencedThumbs);
        ClipboardStorageFileOps.DeleteOrphanedFiles(_store.PayloadDirectory, referencedPayloads);
    }

    private async Task<ClipboardStoreDocument> EnsureLoadedLockedAsync()
    {
        if (_cachedDocument is null)
        {
            if (!_legacyRecentMigrationAttempted)
            {
                _legacyRecentMigrationAttempted = true;
                try
                {
                    if (_quickCaptureService is not null)
                    {
                        await _quickCaptureService.MigrateLegacyRecentItemsToClipboardAsync(_store)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        string? parentDir = Path.GetDirectoryName(
                            _store.RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (!string.IsNullOrWhiteSpace(parentDir))
                        {
                            string siblingQuickCaptureDir = Path.Combine(parentDir, "quick-capture");
                            if (File.Exists(Path.Combine(siblingQuickCaptureDir, "quick-capture.json")))
                            {
                                var siblingQc = new QuickCaptureService(new QuickCaptureStore(siblingQuickCaptureDir));
                                await siblingQc.MigrateLegacyRecentItemsToClipboardAsync(_store)
                                    .ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[Clipboard] Legacy QuickCapture RecentItems migration skipped: {ex.Message}");
                }
            }

            _cachedDocument = await _store.LoadAsync().ConfigureAwait(false);
            RefreshSnapshotsLocked(_cachedDocument);
        }

        return _cachedDocument;
    }

    private void RefreshSnapshotsLocked(ClipboardStoreDocument doc)
    {
        _itemsSnapshot = doc.Items.ToArray();
        _observedAppsSnapshot = doc.ObservedApps
            .OrderByDescending(a => a.LastSeenUtcTicks)
            .Take(MaxObservedApps)
            .ToArray();
    }

    private void ScheduleSave()
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _saveDebounceCts = cts;
        _ = DebouncedSaveAsync(cts.Token);
    }

    private async Task DebouncedSaveAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounceDelay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || _isDisposed)
        {
            return;
        }

        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedDocument is not null)
            {
                await _store.SaveAsync(_cachedDocument).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Clipboard] Save failed: {ex.Message}");
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private async Task FlushSaveLockedAsync(ClipboardStoreDocument doc)
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        _saveDebounceCts = null;
        await _store.SaveAsync(doc).ConfigureAwait(false);
    }

    private void OnSettingsChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        ReconcileChordObserver();

        if (_cachedDocument is null)
        {
            return;
        }

        // Only raise HistoryChanged if tightened retention/max-items settings actually pruned items;
        // visual/appearance settings are handled directly by ClipboardWidgetViewModel.OnSettingsChanged
        // without triggering a duplicate 2,000-item RebuildVisibleItems() pass.
        _ = PruneOnSettingsChangedAsync();
    }

    private void ReconcileChordObserver()
    {
        if (_isDisposed || !_isMonitoring || _isPaused)
        {
            _chordObserver.Configure(observeDoubleCopy: false, observeExternalPaste: false);
            return;
        }

        string doubleCopyMode = _settingsService.Settings.Clipboard.ClipboardDoubleCopyAction;
        bool observeDoubleCopy = !string.IsNullOrWhiteSpace(doubleCopyMode) &&
                                 !string.Equals(doubleCopyMode, ClipboardSettingsSlice.DoubleCopyNone, StringComparison.OrdinalIgnoreCase);

        bool observeExternalPaste;
        lock (_pasteQueueGate)
        {
            observeExternalPaste = _isPasteQueueActive && _pasteQueueIds.Count > 0;
        }

        _chordObserver.Configure(observeDoubleCopy, observeExternalPaste);
    }

    private async Task RestagePasteQueueHeadAfterDelayAsync(ClipboardHistoryItem headItem)
    {
        try
        {
            // Give the source application time to finish delayed format rendering before restoring queue head.
            await Task.Delay(110).ConfigureAwait(false);
            if (_isDisposed)
            {
                return;
            }

            string? currentHeadId;
            lock (_pasteQueueGate)
            {
                currentHeadId = _isPasteQueueActive && _pasteQueueIds.Count > 0 ? _pasteQueueIds[0] : null;
            }

            if (string.Equals(currentHeadId, headItem.Id, StringComparison.Ordinal))
            {
                _ = await CopyItemToClipboardAsync(headItem, forcePlainText: false, recordReuse: false).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Clipboard] PasteQueue restage failed: {ex.Message}");
        }
    }

    private void OnDoubleCopyChordObserved()
    {
        long chordObservedTickMs = Environment.TickCount64;
        _ = HandleDoubleCopyChordAsync(chordObservedTickMs);
    }

    internal void RecordExternalCaptureSkipForTesting(long tickMs) =>
        Interlocked.Exchange(ref _lastExternalCaptureSkipTickMs, tickMs);

    internal async Task<bool> EvaluateDoubleCopyForTestingAsync(
        long chordObservedTickMs,
        ClipboardSourceAppInfo? simulatedForegroundApp = null) =>
        await HandleDoubleCopyChordCoreAsync(
            chordObservedTickMs,
            isTestInvocation: true,
            simulatedForegroundApp: simulatedForegroundApp).ConfigureAwait(false);

    private async Task HandleDoubleCopyChordAsync(long chordObservedTickMs)
    {
        try
        {
            _ = await HandleDoubleCopyChordCoreAsync(chordObservedTickMs, isTestInvocation: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Clipboard] DoubleCopy handler failed: {ex.Message}");
        }
    }

    private async Task<bool> HandleDoubleCopyChordCoreAsync(
        long chordObservedTickMs,
        bool isTestInvocation,
        ClipboardSourceAppInfo? simulatedForegroundApp = null)
    {
        if (!isTestInvocation)
        {
            // Allow WM_CLIPBOARDUPDATE + 100ms capture debounce + persistence to settle.
            await Task.Delay(150).ConfigureAwait(false);
            for (int spin = 0;
                 spin < 10 && (Volatile.Read(ref _capturePendingCount) > 0 || Volatile.Read(ref _captureInFlight) != 0);
                 spin++)
            {
                await Task.Delay(40).ConfigureAwait(false);
            }
        }

        if (_isDisposed || (!isTestInvocation && !_isMonitoring) || _isPaused)
        {
            return false;
        }

        ClipboardSettingsSlice slice = _settingsService.Settings.Clipboard;
        string action = slice.ClipboardDoubleCopyAction;
        if (string.IsNullOrWhiteSpace(action) ||
            string.Equals(action, ClipboardSettingsSlice.DoubleCopyNone, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Never act if the active external foreground window belongs to an excluded app.
        ClipboardSourceAppInfo? fgApp = isTestInvocation
            ? simulatedForegroundApp
            : (ClipboardPlatformInterop.GetForegroundWindow() is var fg && fg != IntPtr.Zero
                ? ClipboardPlatformInterop.DetectSourceApp(fg)
                : null);
        if (fgApp is { } activeApp && IsExcludedApp(activeApp, slice.ClipboardExcludedApps))
        {
            return false;
        }

        // Never act on a previous item if a copy during this chord was skipped (excluded app, privacy format, cut, or secret skip).
        long lastSkipTick = Interlocked.Read(ref _lastExternalCaptureSkipTickMs);
        if (lastSkipTick > 0 && lastSkipTick >= chordObservedTickMs - 750)
        {
            return false;
        }

        // Require an actual external clipboard capture correlated with this double-copy chord window.
        long lastSuccessTick = Interlocked.Read(ref _lastExternalCaptureSuccessTickMs);
        string? capturedId = _lastExternalCapturedItemId;
        if (lastSuccessTick <= 0 ||
            lastSuccessTick < chordObservedTickMs - 850 ||
            string.IsNullOrWhiteSpace(capturedId))
        {
            return false;
        }

        ClipboardHistoryItem? latest = _itemsSnapshot.FirstOrDefault(
            x => string.Equals(x.Id, capturedId, StringComparison.Ordinal));
        if (latest is null)
        {
            return false;
        }

        if (string.Equals(action, ClipboardSettingsSlice.DoubleCopyPin, StringComparison.OrdinalIgnoreCase))
        {
            if (!latest.IsPinned)
            {
                await SetPinnedBatchAsync([latest.Id], isPinned: true).ConfigureAwait(false);
            }

            DoubleCopyActionExecuted?.Invoke(this, new ClipboardDoubleCopyEventArgs(latest, ClipboardSettingsSlice.DoubleCopyPin));
            return true;
        }

        if (string.Equals(action, ClipboardSettingsSlice.DoubleCopyQuickCapture, StringComparison.OrdinalIgnoreCase))
        {
            DoubleCopyActionExecuted?.Invoke(this, new ClipboardDoubleCopyEventArgs(latest, ClipboardSettingsSlice.DoubleCopyQuickCapture));
            return true;
        }

        return false;
    }

    private void OnExternalPasteChordObserved()
    {
        _ = HandleExternalPasteChordAsync();
    }

    private async Task HandleExternalPasteChordAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await _externalPasteAdvanceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Give the target application ~115ms to read the current clipboard item before advancing FIFO,
            // serialized so rapid Ctrl+V chords never race or skip items.
            await Task.Delay(115).ConfigureAwait(false);
            if (_isDisposed)
            {
                return;
            }

            await AdvancePasteQueueInternalAsync(stageNextToClipboard: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Clipboard] PasteQueue external advance failed: {ex.Message}");
        }
        finally
        {
            _externalPasteAdvanceLock.Release();
        }
    }

    private async Task TryPopulateImageOcrInBackgroundAsync(string itemId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            _ = await ExtractAndCacheOcrTextAsync(itemId, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Background OCR indexing is best-effort and silent.
        }
    }

    private async Task PruneOnSettingsChangedAsync()
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed || _cachedDocument is null)
            {
                return;
            }

            if (PruneExpiredAndExcessItemsLocked(_cachedDocument))
            {
                ScheduleSave();
                RaiseHistoryChanged();
            }
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private void RaiseHistoryChanged()
    {
        if (_cachedDocument is not null)
        {
            RefreshSnapshotsLocked(_cachedDocument);
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelCaptureDebounce()
    {
        _captureDebounceCts?.Cancel();
        _captureDebounceCts?.Dispose();
        _captureDebounceCts = null;
    }

    private static void TryFlushClipboard()
    {
        try
        {
            Clipboard.Flush();
        }
        catch
        {
            // Clipboard content is already set; Flush can transiently fail if another app opens clipboard.
        }
    }

    public async Task FlushAsync()
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedDocument is not null)
            {
                await FlushSaveLockedAsync(_cachedDocument).ConfigureAwait(false);
            }
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        lock (_foregroundTrackingGate)
        {
            _activeForegroundTrackingViews.Clear();
        }

        _settingsService.SettingsChanged -= OnSettingsChanged;
        StopMonitoring();
        _chordObserver.Dispose();
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        _saveDebounceCts = null;
    }
}
